using System.IO.Compression;
using System.Xml.Linq;
using Cya2.Application.Interfaces;
using Cya2.Application.Services;
using Cya2.Core.Imports;
using Cya2.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfficeOpenXml;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class ImportPreviewValidationTests
{
    private static readonly string[] Headers =
    [
        "Gift Date", "Name", "Gift Payment Type", "Gift Type", "Fund Split Amount", "Fund Notes",
        "Soft Credit Recipient Name", "Preferred Address Line 1", "Preferred City", "Preferred State",
        "Preferred ZIP", "Preferred Country", "Personal Email Number", "Home Phone Number",
        "Personal Mobile Phone Number", "Gift Is Anonymous", "Gift Import ID", "Primary Addressee",
        "Constituent ID", "Soft Credit Recipient ID", "Tribute System Record ID", "Honor/Memorial Name"
    ];

    [Fact]
    public async Task DonationPreviewReadsWorkbookOnceAndRetainsOnlyOriginalBytes()
    {
        var reader = new CountingReader();
        var (service, previews) = CreateService(reader);
        using var workbook = Workbook("valid");
        var expected = workbook.ToArray();

        var result = await service.PreviewAsync(workbook, "DoNaTiOnS", "synthetic.xlsx", "application/test");

        Assert.Equal(1, reader.Calls);
        Assert.Equal(1, result.CanonicalDonationCount);
        Assert.Equal(new DateOnly(2025, 1, 2), result.MinimumDonationDate);
        Assert.Equal(result.MinimumDonationDate, result.MaximumDonationDate);
        Assert.True(previews.TryRemoveOwned(result.PreviewId, "synthetic", "donations", out var retained));
        Assert.NotNull(retained);
        Assert.Equal(expected, retained.Data);
        Assert.False(await service.DiscardPreviewAsync(result.PreviewId, "donations"));
    }

    [Theory]
    [InlineData("malformed", "valid Excel workbook")]
    [InlineData("no-worksheets", "No worksheet found")]
    [InlineData("semantic", "Missing column: Gift Date")]
    public async Task DonationPreviewRejectsInvalidWorkbooksAndReleasesCapacity(string kind, string message)
    {
        var reader = new CountingReader();
        var (service, _) = CreateService(reader);
        using var workbook = Workbook(kind);

        var exception = await Assert.ThrowsAsync<ImportUploadValidationException>(() =>
            service.PreviewAsync(workbook, "donations", "synthetic.xlsx", "application/test"));

        Assert.Contains(message, exception.Message);
        Assert.Equal(1, reader.Calls);
        using var retry = Workbook("valid");
        var preview = await service.PreviewAsync(retry, "donations", "synthetic.xlsx", "application/test");
        Assert.Equal(2, reader.Calls);
        Assert.True(await service.DiscardPreviewAsync(preview.PreviewId, "donations"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("no-worksheets")]
    public async Task AccountingPreviewStillRejectsInvalidWorkbooksWithoutDonationReader(string kind)
    {
        var reader = new CountingReader();
        var (service, _) = CreateService(reader);
        using var workbook = Workbook(kind);

        var exception = await Assert.ThrowsAsync<ImportUploadValidationException>(() =>
            service.PreviewAsync(workbook, "accounting", "synthetic.xlsx", "application/test"));

        Assert.Contains("valid Excel workbook", exception.Message);
        Assert.Equal(0, reader.Calls);
        using var retry = Workbook("semantic");
        var preview = await service.PreviewAsync(retry, "accounting", "synthetic.xlsx", "application/test");
        Assert.Null(preview.CanonicalDonationCount);
        Assert.True(await service.DiscardPreviewAsync(preview.PreviewId, "accounting"));
    }

    [Fact]
    public async Task AccountingPreviewDoesNotRequireDonationReader()
    {
        var (service, _) = CreateService(null);
        using var workbook = Workbook("semantic");

        var preview = await service.PreviewAsync(workbook, "accounting", "synthetic.xlsx", "application/test");

        Assert.Null(preview.CanonicalDonationCount);
        Assert.True(await service.DiscardPreviewAsync(preview.PreviewId, "accounting"));
    }

    [Fact]
    public async Task MissingDonationReaderRemainsConfigurationError()
    {
        var (service, _) = CreateService(null);
        using var workbook = Workbook("valid");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(workbook, "donations", "synthetic.xlsx", "application/test"));

        Assert.Contains("validation is unavailable", exception.Message);
    }

    [Fact]
    public async Task DonationReaderCancellationIsNotTranslatedAndReleasesCapacity()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException(cancellation.Token);
        var reader = new CountingReader { Failure = expected };
        var (service, _) = CreateService(reader);
        using var workbook = Workbook("valid");

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.PreviewAsync(workbook, "donations", "synthetic.xlsx", "application/test", cancellation.Token));

        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, reader.LastToken);
        reader.Failure = null;
        using var retry = Workbook("valid");
        var preview = await service.PreviewAsync(retry, "donations", "synthetic.xlsx", "application/test");
        Assert.True(await service.DiscardPreviewAsync(preview.PreviewId, "donations"));
    }

    [Fact]
    public async Task PreCancelledPreviewDoesNotInvokeDonationReader()
    {
        var reader = new CountingReader();
        var (service, _) = CreateService(reader);
        using var workbook = Workbook("valid");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PreviewAsync(workbook, "donations", "synthetic.xlsx", "application/test", cancellation.Token));

        Assert.Equal(0, reader.Calls);
    }

    private static (ImportOrchestrationService Service, ImportPreviewStore Previews) CreateService(IDonationWorkbookReader? reader)
    {
        var options = Options.Create(new ImportUploadOptions
        {
            MaximumConcurrentUploads = 1,
            MaximumRetainedPreviews = 1
        });
        var previews = new ImportPreviewStore(options);
        var service = new ImportOrchestrationService(
            [new PreviewOnlyProcessor("donations"), new PreviewOnlyProcessor("accounting")],
            null!, null!, previews, new ImportUploadValidator(options), new SyntheticAuthorization(),
            null!, NullLogger<ImportOrchestrationService>.Instance, reader);
        return (service, previews);
    }

    private static MemoryStream Workbook(string kind)
    {
        ExcelPackage.License.SetNonCommercialOrganization("Servant Partners");
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Synthetic");
        for (var index = 0; index < Headers.Length; index++) sheet.Cells[1, index + 1].Value = Headers[index];
        sheet.Cells[2, 1].Value = "01/02/2025";
        sheet.Cells[2, 2].Value = "Synthetic Person";
        sheet.Cells[2, 3].Value = "Check";
        sheet.Cells[2, 4].Value = "Cash";
        sheet.Cells[2, 5].Value = "25.50";
        sheet.Cells[2, 6].Value = "Synthetic Fund";
        sheet.Cells[2, 17].Value = "gift-001";
        sheet.Cells[2, 18].Value = "Synthetic Person";
        sheet.Cells[2, 19].Value = "000person";
        if (kind == "semantic") sheet.Cells[1, 1].Value = "Other";
        var bytes = package.GetAsByteArray();
        if (kind is not ("malformed" or "no-worksheets")) return new MemoryStream(bytes);

        using var original = new MemoryStream(bytes);
        using var source = new ZipArchive(original, ZipArchiveMode.Read);
        var result = new MemoryStream();
        using (var target = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var output = target.CreateEntry(entry.FullName).Open();
                if (entry.FullName != "xl/workbook.xml") input.CopyTo(output);
                else if (kind == "malformed")
                {
                    using var writer = new StreamWriter(output, leaveOpen: true);
                    writer.Write("<workbook><broken>");
                }
                else
                {
                    var xml = XDocument.Load(input);
                    xml.Root?.Element(xml.Root.GetDefaultNamespace() + "sheets")?.RemoveNodes();
                    xml.Save(output);
                }
            }
        }
        result.Position = 0;
        return result;
    }

    private sealed class CountingReader : IDonationWorkbookReader
    {
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Exception? Failure { get; set; }

        public DonationUploadValidationResult Read(Stream file, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastToken = cancellationToken;
            if (Failure is not null) throw Failure;
            return new DonationWorkbookReader().Read(file, cancellationToken);
        }
    }

    private sealed class SyntheticAuthorization : IImportAuthorizationContext
    {
        public Task<ImportActor?> GetCurrentActorAsync() => Task.FromResult<ImportActor?>(new ImportActor("synthetic", true));
    }

    private sealed class PreviewOnlyProcessor(string importType) : IImportProcessor
    {
        public string ImportType => importType;

        public Task<ImportResult> ProcessAsync(Stream file, string progressId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Preview validation must never perform an import.");
    }
}
