using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cya2.Core.DTOs;

namespace Cya2.Application.Interfaces;

public sealed class FilePreviewResult
{
    public string PreviewId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public int? CanonicalDonationCount { get; set; }
    public int DuplicateRowsMerged { get; set; }
    public int? UnresolvedNormalDonationCount { get; set; }
    public int? UnresolvedInternDonationCount { get; set; }
    public DateOnly? MinimumDonationDate { get; set; }
    public DateOnly? MaximumDonationDate { get; set; }
    public bool RequiresOldDateAcknowledgment { get; set; }
}

public sealed record ImportActor(string UserId, bool IsAdmin);

public interface IImportAuthorizationContext
{
    Task<ImportActor?> GetCurrentActorAsync();
}

public sealed class ImportResult
{
    public int TotalRows { get; set; }
    public int InsertedRows { get; set; }
    public int FailedRows { get; set; }
    public bool PublicationCommitted { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public int DuplicateRowsMerged { get; set; }
    public List<DonationImportReportEntry> DuplicateReport { get; } = new();
    public string? ProgressId { get; set; }
}

public interface IImportProcessor
{
    string ImportType { get; }
    Task<ImportResult> ProcessAsync(Stream file, string progressId, CancellationToken cancellationToken);
}

public interface IImportOrchestrationService
{
    Task<bool> CancelImportAsync(string progressId) => Task.FromResult(false);
    Task<bool> DiscardPreviewAsync(string previewId, string importType) => Task.FromResult(false);
    Task<FilePreviewResult> PreviewAsync(Stream file, string importType, string fileName, string contentType, CancellationToken cancellationToken = default);
    Task<ImportResult> ImportFromPreviewAsync(string previewId, string importType, CancellationToken cancellationToken = default);
    Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, string? progressId = null);
    Task<ImportResult> ImportFromPreviewAsync(string previewId, string importType, bool oldDateAcknowledged, CancellationToken cancellationToken = default)
        => ImportFromPreviewAsync(previewId, importType, cancellationToken);
    Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, bool oldDateAcknowledged)
        => StartImportFromPreviewAsync(previewId, importType);
    Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, bool oldDateAcknowledged, string progressId)
        => StartImportFromPreviewAsync(previewId, importType, oldDateAcknowledged);
}
