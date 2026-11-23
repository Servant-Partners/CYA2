using System.Threading.Channels;
using Cya2.Application.Interfaces;
using Cya2.Application.Diagnostics;
using Cya2.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Cya2.Core.Entities;
using Cya2.Application.Services;

namespace cya2.Services.Imports;

public sealed class ImportWorkQueue : BackgroundService, IImportWorkQueue
{
    private readonly Channel<ImportWorkItem> _queue = Channel.CreateBounded<ImportWorkItem>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImportWorkQueue> _logger;
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, (int? Owner, CancellationTokenSource Cancellation, long Bytes)> _admitted = new(StringComparer.Ordinal);
    private long _admittedBytes;

    public ImportWorkQueue(IServiceScopeFactory scopeFactory, ILogger<ImportWorkQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _logger.LogInformation("Import work queue instance created.");
    }

    public ValueTask EnqueueAsync(ImportWorkItem workItem, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_admissionLock)
        {
            if (_admitted.Count >= 2 || _admittedBytes > 2 * ImportUploadOptions.DefaultMaximumUploadBytes - workItem.Data.LongLength)
                throw new ImportUploadCapacityException("The import queue is full. Wait for an existing import to finish or cancel it, then retry this preview.");
            if (_admitted.ContainsKey(workItem.ProgressId))
                throw new ImportUploadValidationException("This import has already been submitted.");
            var cancellation = new CancellationTokenSource();
            _admitted.Add(workItem.ProgressId, (workItem.ActorUserId, cancellation, workItem.Data.LongLength));
            _admittedBytes += workItem.Data.LongLength;
            if (!_queue.Writer.TryWrite(workItem))
            {
                ReleaseAdmission(workItem.ProgressId);
                throw new ImportUploadCapacityException("The import queue is unavailable. Please retry this preview shortly.");
            }
        }
        _logger.LogInformation(
            "Import work item enqueueing. ImportType={ImportType}, PreviewId={PreviewId}, ProgressId={ProgressId}, Bytes={Bytes}",
            workItem.ImportType, workItem.PreviewId, workItem.ProgressId, workItem.Data.Length);
        _logger.LogInformation(
            "Import work item enqueued. ImportType={ImportType}, PreviewId={PreviewId}, ProgressId={ProgressId}",
            workItem.ImportType, workItem.PreviewId, workItem.ProgressId);
        return ValueTask.CompletedTask;
    }

    public bool RequestCancellation(string progressId, int actorUserId)
    {
        lock (_admissionLock)
        {
            if (!_admitted.TryGetValue(progressId, out var admission) || admission.Owner != actorUserId)
                return false;
            admission.Cancellation.Cancel();
            return true;
        }
    }

    private void ReleaseAdmission(string progressId)
    {
        lock (_admissionLock)
        {
            if (!_admitted.Remove(progressId, out var admission)) return;
            _admittedBytes -= admission.Bytes;
            admission.Cancellation.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Import work queue worker started.");
            await foreach (var workItem in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var operation = AuditOperationScope.Begin(workItem.ActorUserId, workItem.ProgressId);
                _logger.LogInformation(
                    "Import work item dequeued. ImportType={ImportType}, PreviewId={PreviewId}, ProgressId={ProgressId}",
                    workItem.ImportType, workItem.PreviewId, workItem.ProgressId);
                try
                {
                    CancellationToken requestedCancellation;
                    lock (_admissionLock) requestedCancellation = _admitted[workItem.ProgressId].Cancellation.Token;
                    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, requestedCancellation);
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    using var scope = _scopeFactory.CreateScope();
                    var actor = workItem.ActorUserId is { } actorId
                        ? await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(actorId)
                        : null;
                    if (actor is null || !string.Equals(actor.AuthLevel, "Admin", StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException("Administrator access is no longer available.");
                    var processor = scope.ServiceProvider.GetRequiredService<IEnumerable<IImportProcessor>>()
                        .Single(item => string.Equals(item.ImportType, workItem.ImportType, StringComparison.OrdinalIgnoreCase));
                    var auditEvents = scope.ServiceProvider.GetRequiredService<IAuditEventService>();
                    await using var stream = new MemoryStream(workItem.Data, writable: false);
                    _logger.LogInformation("Background {ImportType} processor started. PreviewId={PreviewId}, ProgressId={ProgressId}", workItem.ImportType, workItem.PreviewId, workItem.ProgressId);
                    var importResult = await processor.ProcessAsync(stream, workItem.ProgressId, linkedCancellation.Token);
                    try
                    {
                        if (!importResult.PublicationCommitted) await auditEvents.RecordAsync(
                        workItem.ActorUserId,
                        GetAuditEventType(workItem.ImportType, AuditOutcomes.Failed),
                        AuditOutcomes.Failed,
                        itemCount: importResult.InsertedRows,
                        cancellationToken: CancellationToken.None);
                    }
                    catch
                    {
                        _logger.LogWarning("Background import terminal audit delivery failed. ProgressId={ProgressId}", workItem.ProgressId);
                    }
                    _logger.LogInformation("Background {ImportType} processor returned. PreviewId={PreviewId}, ProgressId={ProgressId}", workItem.ImportType, workItem.PreviewId, workItem.ProgressId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await ReportFailureAsync(workItem, new OperationCanceledException(), CancellationToken.None);
                    break;
                }
                catch (Exception ex)
                {
                    await ReportFailureAsync(workItem, ex, CancellationToken.None);
                }
                finally
                {
                    ReleaseAdmission(workItem.ProgressId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Import work queue worker stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical("Import work queue worker stopped unexpectedly. ExceptionType={ExceptionType}", ex.GetType().Name);
            throw new InvalidOperationException("The import work queue worker stopped unexpectedly.");
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var pending))
            {
                try { await ReportFailureAsync(pending, new OperationCanceledException(), CancellationToken.None); }
                finally { ReleaseAdmission(pending.ProgressId); }
            }
        }
    }

    private async Task ReportFailureAsync(ImportWorkItem workItem, Exception exception, CancellationToken cancellationToken)
    {
        using var operation = AuditOperationScope.Begin(workItem.ActorUserId, workItem.ProgressId);
        var errorMessage = exception is ImportOutcomeUnknownException ? exception.Message
            : exception is OperationCanceledException ? "Import cancelled before publication completed."
            : ImportDiagnosticMessages.ImportFailed();
        if (exception is MySqlException mySqlException)
        {
            _logger.LogError(
                "Background {ImportType} import failed for preview {PreviewId}. ExceptionType={ExceptionType}, MySqlErrorCode={MySqlErrorCode}, SqlState={SqlState}",
                workItem.ImportType,
                workItem.PreviewId,
                ImportDiagnosticMessages.ExceptionCategory(exception),
                mySqlException.Number,
                mySqlException.SqlState);
        }
        else
        {
            _logger.LogError("Background {ImportType} import failed for preview {PreviewId}. ExceptionType={ExceptionType}",
                workItem.ImportType, workItem.PreviewId, ImportDiagnosticMessages.ExceptionCategory(exception));
        }
        try
        {
            using var errorScope = _scopeFactory.CreateScope();
            var auditEvents = errorScope.ServiceProvider.GetRequiredService<IAuditEventService>();
            await auditEvents.RecordAsync(
                workItem.ActorUserId,
                GetAuditEventType(workItem.ImportType, AuditOutcomes.Failed),
                exception is ImportOutcomeUnknownException ? AuditOutcomes.Unknown : exception is OperationCanceledException ? AuditOutcomes.Cancelled : AuditOutcomes.Failed,
                cancellationToken: cancellationToken);
        }
        catch (Exception reportingException)
        {
            _logger.LogError("Could not report failed import {ProgressId}. ExceptionType={ExceptionType}",
                workItem.ProgressId, ImportDiagnosticMessages.ExceptionCategory(reportingException));
        }
        try
        {
            using var progressScope = _scopeFactory.CreateScope();
            var progress = progressScope.ServiceProvider.GetRequiredService<IImportProgressService>();
            progress.AddErrors(workItem.ProgressId, new[] { errorMessage });
            progress.SetStatus(workItem.ProgressId, errorMessage);
            progress.Complete(workItem.ProgressId);
        }
        catch (Exception reportingException)
        {
            _logger.LogError("Could not report terminal import progress {ProgressId}. ExceptionType={ExceptionType}",
                workItem.ProgressId, ImportDiagnosticMessages.ExceptionCategory(reportingException));
        }
    }

    private static string GetAuditEventType(string importType, string outcome)
        => string.Equals(importType, "donations", StringComparison.OrdinalIgnoreCase)
            ? outcome == AuditOutcomes.Succeeded ? AuditEventTypes.DonationImportCompleted : AuditEventTypes.DonationImportFailed
            : outcome == AuditOutcomes.Succeeded ? AuditEventTypes.AccountingImportCompleted : AuditEventTypes.AccountingImportFailed;
}