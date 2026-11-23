using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cya2.Application.Interfaces;
using Cya2.Application.Services;
using Cya2.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfficeOpenXml;
using Microsoft.VSDiagnostics;

[ShortRunJob]
[CPUUsageDiagnoser]
public class DonationPreviewValidationBenchmarks
{
    private byte[] _data = null!;
    private ImportPreviewStore _previews = null!;
    private ImportOrchestrationService _service = null!;
    [GlobalSetup]
    public void Setup()
    {
        ExcelPackage.License.SetNonCommercialOrganization("Servant Partners");
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Synthetic");
        string[] headers = ["Gift Date", "Name", "Gift Payment Type", "Gift Type", "Fund Split Amount", "Fund Notes", "Soft Credit Recipient Name", "Preferred Address Line 1", "Preferred City", "Preferred State", "Preferred ZIP", "Preferred Country", "Personal Email Number", "Home Phone Number", "Personal Mobile Phone Number", "Gift Is Anonymous", "Gift Import ID", "Primary Addressee", "Constituent ID", "Soft Credit Recipient ID", "Tribute System Record ID", "Honor/Memorial Name"];
        for (var index = 0; index < headers.Length; index++)
            sheet.Cells[1, index + 1].Value = headers[index];
        for (var row = 2; row <= 2001; row++)
        {
            sheet.Cells[row, 1].Value = "01/02/2025";
            sheet.Cells[row, 2].Value = "Synthetic Person";
            sheet.Cells[row, 3].Value = "Check";
            sheet.Cells[row, 4].Value = "Cash";
            sheet.Cells[row, 5].Value = "25.50";
            sheet.Cells[row, 6].Value = "Synthetic Fund";
            sheet.Cells[row, 17].Value = "gift-" + row;
            sheet.Cells[row, 18].Value = "Synthetic Person";
            sheet.Cells[row, 19].Value = "000person";
        }

        _data = package.GetAsByteArray();
        var options = Options.Create(new ImportUploadOptions());
        _previews = new ImportPreviewStore(options);
        _service = new ImportOrchestrationService([new PreviewOnlyProcessor("donations"), new PreviewOnlyProcessor("accounting")], null!, null!, _previews, new ImportUploadValidator(options), new SyntheticAuthorization(), null!, NullLogger<ImportOrchestrationService>.Instance, new DonationWorkbookReader());
    }

    [Benchmark]
    public Task<int> DonationPreview() => Preview("donations");
    [Benchmark]
    public Task<int> AccountingPreview() => Preview("accounting");
    private async Task<int> Preview(string importType)
    {
        using var stream = new MemoryStream(_data, writable: false);
        var preview = await _service.PreviewAsync(stream, importType, "synthetic.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        if (!_previews.TryRemove(preview.PreviewId, out _))
            throw new InvalidOperationException("Preview cleanup failed.");
        return preview.CanonicalDonationCount ?? 0;
    }

    private sealed class SyntheticAuthorization : IImportAuthorizationContext
    {
        public Task<ImportActor> GetCurrentActorAsync() => Task.FromResult(new ImportActor("synthetic", true));
    }

    private sealed class PreviewOnlyProcessor(string importType) : IImportProcessor
    {
        public string ImportType => importType;

        public Task<ImportResult> ProcessAsync(Stream file, string progressId, CancellationToken cancellationToken) => throw new InvalidOperationException("This benchmark must never execute an import.");
    }
}