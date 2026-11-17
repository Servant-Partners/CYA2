using System.Collections.Concurrent;
using Cya2.Application.Interfaces;
using Cya2.Core.Interfaces;
using Cya2.Core.Entities;
using Microsoft.Extensions.Logging;
using Cya2.Core.Imports;

namespace Cya2.Application.Services;

public sealed class ImportOrchestrationService : IImportOrchestrationService
{
    private readonly IReadOnlyDictionary<string, IImportProcessor> _processors;
    private readonly IImportProgressService _progressService;
    private readonly IImportWorkQueue _workQueue;
    private readonly ILogger<ImportOrchestrationService> _logger;
    private readonly ImportPreviewStore _previews;
    private readonly ImportUploadValidator _uploadValidator;
    private readonly IImportAuthorizationContext _authorizationContext;
    private readonly IAuditEventService _auditEvents;
    private readonly IDonationWorkbookReader? _donationReader;
    private readonly TimeProvider _clock;
    private readonly IDonationAccountMappingReader? _accountMappingReader;

    public ImportOrchestrationService(
        IEnumerable<IImportProcessor> processors,
        IImportProgressService progressService,
        IImportWorkQueue workQueue,
        ImportPreviewStore previews,
        ImportUploadValidator uploadValidator,
        IImportAuthorizationContext authorizationContext,
        IAuditEventService auditEvents,
        ILogger<ImportOrchestrationService> logger,
        IDonationWorkbookReader? donationReader = null,
        TimeProvider? clock = null,
        IDonationAccountMappingReader? accountMappingReader = null)
    {
        _processors = processors.ToDictionary(p => p.ImportType, StringComparer.OrdinalIgnoreCase);
        _progressService = progressService;
        _workQueue = workQueue;
        _previews = previews;
        _uploadValidator = uploadValidator;
        _authorizationContext = authorizationContext;
        _auditEvents = auditEvents;
        _logger = logger;
        _donationReader = donationReader;
        _clock = clock ?? TimeProvider.System;
        _accountMappingReader = accountMappingReader;
    }

    public async Task<FilePreviewResult> PreviewAsync(
        Stream file,
        string importType,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        GetProcessor(importType);

        var actor = await _authorizationContext.GetCurrentActorAsync();
        if (actor is null || !actor.IsAdmin)
            throw new ImportAuthorizationException();

        DonationUploadValidationResult? semantic = null;
        Action<byte[]>? validateWorkbook = null;
        if (string.Equals(importType, "donations", StringComparison.OrdinalIgnoreCase))
        {
            validateWorkbook = bytes =>
            {
                if (_donationReader is null) throw new InvalidOperationException("Donation workbook validation is unavailable.");
                using var workbook = new MemoryStream(bytes, writable: false);
                try
                {
                    semantic = _donationReader.Read(workbook, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw new ImportUploadValidationException("The uploaded file is not a valid Excel workbook.");
                }
                if (!semantic.IsValid)
                    throw new ImportUploadValidationException(string.Join(Environment.NewLine, semantic.Errors.Select(error => error.Message)));
            };
        }
        var data = await _uploadValidator.ReadAndValidateAsync(file, fileName, cancellationToken, validateWorkbook);

        var mappings = semantic is not null && _accountMappingReader is not null
            ? await _accountMappingReader.GetCountsAsync(semantic.Donations, cancellationToken)
            : null;
        var previewId = Guid.NewGuid().ToString("N");
        if (!_previews.TrySet(previewId, new ImportPreview(data, fileName ?? string.Empty, contentType ?? string.Empty, _clock.GetUtcNow().UtcDateTime, actor.UserId, importType) { MinimumDonationDate = semantic?.MinimumDate }))
            throw new ImportUploadCapacityException("The application is at its limit for retained upload previews. Please try again after an existing preview is imported or expires.");

        _logger.LogInformation("Created {ImportType} import preview {PreviewId} ({Size} bytes)", importType, previewId, data.Length);

        return new FilePreviewResult
        {
            PreviewId = previewId,
            FileName = fileName ?? string.Empty,
            FileSizeBytes = data.Length,
            ContentType = contentType ?? string.Empty,
            CanonicalDonationCount = semantic?.Donations.Length,
            DuplicateRowsMerged = semantic?.DuplicateRowsMerged ?? 0,
            MinimumDonationDate = semantic?.MinimumDate,
            MaximumDonationDate = semantic?.MaximumDate,
            UnresolvedNormalDonationCount = mappings?.UnresolvedNormalDonations,
            UnresolvedInternDonationCount = mappings?.UnresolvedInternDonations,
            RequiresOldDateAcknowledgment = semantic?.MinimumDate is { } minimum && RequiresAcknowledgment(minimum)
        };
    }

    public Task<ImportResult> ImportFromPreviewAsync(string previewId, string importType, CancellationToken cancellationToken = default)
        => ImportFromPreviewAsync(previewId, importType, false, cancellationToken);

    public async Task<ImportResult> ImportFromPreviewAsync(string previewId, string importType, bool oldDateAcknowledged, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(previewId))
            throw new ArgumentException("PreviewId is required", nameof(previewId));

        var actor = await _authorizationContext.GetCurrentActorAsync();
        if (actor is null || !actor.IsAdmin || !_previews.TryRemoveOwned(previewId, actor.UserId, importType, out var preview, candidate => ValidateAcceptance(candidate, oldDateAcknowledged)) || preview is null)
        {
            var expired = new ImportResult();
            expired.Errors.Add("Preview session expired. Please upload the file again.");
            return expired;
        }

        var progressId = Guid.NewGuid().ToString("N");
        using var operation = AuditOperationScope.Begin(int.TryParse(actor.UserId, out var actorId) ? actorId : null, progressId);
        _progressService.Start(progressId, importType, actor.UserId);
        try
        {
            await RecordImportAuditAsync(actor.UserId, importType, AuditOutcomes.Started, cancellationToken: cancellationToken);
            await using var stream = new MemoryStream(preview.Data, writable: false);
            var importResult = await GetProcessor(importType).ProcessAsync(stream, progressId, cancellationToken);
            try
            {
                if (!importResult.PublicationCommitted)
                    await RecordImportAuditAsync(actor.UserId, importType, AuditOutcomes.Failed, importResult.InsertedRows, CancellationToken.None);
            }
            catch
            {
                importResult.Warnings.Add("The upload result could not be recorded in the audit log.");
                _logger.LogWarning("Import terminal audit delivery failed.");
            }
            return importResult;
        }
        catch (Exception ex)
        {
            var message = ex is ImportOutcomeUnknownException ? ex.Message
                : ex is OperationCanceledException ? "Import cancelled before publication completed."
                : "Import failed. Existing data was not replaced unless the commit outcome requires reconciliation.";
            _progressService.AddErrors(progressId, [message]);
            _progressService.SetStatus(progressId, message);
            _progressService.Complete(progressId);
            await RecordImportAuditAsync(actor.UserId, importType,
                ex is ImportOutcomeUnknownException ? AuditOutcomes.Unknown : ex is OperationCanceledException ? AuditOutcomes.Cancelled : AuditOutcomes.Failed,
                cancellationToken: CancellationToken.None);
            throw;
        }
    }

    public Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, string? progressId = null)
        => StartImportFromPreviewAsync(previewId, importType, false, progressId ?? Guid.NewGuid().ToString("N"));

    public async Task<bool> CancelImportAsync(string progressId)
    {
        var actor = await _authorizationContext.GetCurrentActorAsync();
        return actor is { IsAdmin: true } && int.TryParse(actor.UserId, out var actorId)
            && _workQueue.RequestCancellation(progressId, actorId);
    }

    public async Task<bool> DiscardPreviewAsync(string previewId, string importType)
    {
        var actor = await _authorizationContext.GetCurrentActorAsync();
        return actor is { IsAdmin: true } && _previews.TryRemoveOwned(previewId, actor.UserId, importType, out _);
    }

    public Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, bool oldDateAcknowledged)
        => StartImportFromPreviewAsync(previewId, importType, oldDateAcknowledged, Guid.NewGuid().ToString("N"));

    public async Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, bool oldDateAcknowledged, string progressId)
    {
        var result = new ImportResult { ProgressId = progressId };
        var actor = await _authorizationContext.GetCurrentActorAsync();
        if (actor is null || !actor.IsAdmin)
        {
            result.Errors.Add("The import is no longer available.");
            return result;
        }

        if (string.IsNullOrWhiteSpace(previewId))
        {
            result.Errors.Add("PreviewId is required");
            _progressService.SetStatus(result.ProgressId, "PreviewId is required");
            return result;
        }

        if (!_previews.TryClaimOwned(previewId, actor.UserId, importType, out var preview, candidate => ValidateAcceptance(candidate, oldDateAcknowledged)) || preview is null)
        {
            result.Errors.Add("Preview session expired. Please upload the file again.");
            result.ProgressId = null;
            return result;
        }

        using var operation = AuditOperationScope.Begin(int.TryParse(actor.UserId, out var auditActorId) ? auditActorId : null, result.ProgressId);
        var id = result.ProgressId;
        var admitted = false;
        try
        {
            _progressService.Start(result.ProgressId!, importType, actor.UserId);
            _progressService.AddStep(progressId, "ImportSubmission", "ImportSubmission");
            await RecordImportAuditAsync(actor.UserId, importType, AuditOutcomes.Started);
            _progressService.CompleteStep(progressId, "ImportSubmission", "Completed");
            _progressService.AddStep(progressId, "ImportQueued", "ImportQueued");
            _logger.LogInformation("Queueing background {ImportType} import. PreviewId={PreviewId}, ProgressId={ProgressId}", importType, previewId, id);
            await _workQueue.EnqueueAsync(new ImportWorkItem(
                importType,
                previewId,
                id,
                int.TryParse(actor.UserId, out var actorUserId) ? actorUserId : null,
                preview.Data));
            admitted = true;
        }
        catch (Exception ex)
        {
            try
            {
                await RecordImportAuditAsync(actor.UserId, importType, ex is OperationCanceledException ? AuditOutcomes.Cancelled : AuditOutcomes.Failed, cancellationToken: CancellationToken.None);
            }
            catch
            {
                _logger.LogWarning("Import admission failure audit delivery failed.");
            }
            _progressService.AddErrors(id!, [ex is ImportUploadValidationException ? ex.Message : "Import did not start. No publication was attempted."]);
            _progressService.SetStatus(id!, "Import did not start. No publication was attempted.");
            _progressService.Complete(id!);
            throw;
        }
        finally
        {
            _previews.ReleaseClaim(previewId, admitted);
        }

        return result;
    }

    private bool RequiresAcknowledgment(DateOnly boundary) => DonationReplacementPolicy.RequiresOldDateConfirmation(
        boundary, DateOnly.FromDateTime(_clock.GetLocalNow().DateTime));

    private bool ValidateAcceptance(ImportPreview preview, bool acknowledged)
    {
        if (!string.Equals(preview.ImportType, "donations", StringComparison.OrdinalIgnoreCase)) return true;
        if (preview.MinimumDonationDate is not { } boundary)
            throw new ImportUploadValidationException("Please upload the donation workbook again for validation.");
        if (RequiresAcknowledgment(boundary) && !acknowledged)
            throw new ImportUploadValidationException("Explicit acknowledgment is required: the replacement date is more than 380 days old. All donations on or after that date will be replaced, without an upper date limit.");
        return true;
    }

    private IImportProcessor GetProcessor(string importType)
        => _processors.TryGetValue(importType, out var processor)
            ? processor
            : throw new ArgumentException($"Invalid import type: {importType}", nameof(importType));

    private Task RecordImportAuditAsync(
        string actorUserId,
        string importType,
        string outcome,
        int? itemCount = null,
        CancellationToken cancellationToken = default)
    {
        var eventType = string.Equals(importType, "donations", StringComparison.OrdinalIgnoreCase)
            ? outcome switch
            {
                AuditOutcomes.Started => AuditEventTypes.DonationImportStarted,
                AuditOutcomes.Succeeded => AuditEventTypes.DonationImportCompleted,
                _ => AuditEventTypes.DonationImportFailed
            }
            : outcome switch
            {
                AuditOutcomes.Started => AuditEventTypes.AccountingImportStarted,
                AuditOutcomes.Succeeded => AuditEventTypes.AccountingImportCompleted,
                _ => AuditEventTypes.AccountingImportFailed
            };

        var actorId = int.TryParse(actorUserId, out var parsedActorId) ? parsedActorId : (int?)null;
        if (outcome == AuditOutcomes.Started)
            return _auditEvents.RecordRequiredAsync(actorId, eventType, outcome, itemCount: itemCount, cancellationToken: cancellationToken);
        return _auditEvents.RecordAsync(
            actorId,
            eventType,
            outcome,
            itemCount: itemCount,
            cancellationToken: cancellationToken);
    }
}
