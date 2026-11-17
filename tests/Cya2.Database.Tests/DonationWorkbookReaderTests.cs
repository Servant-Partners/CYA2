using Cya2.Infrastructure.Services;
using OfficeOpenXml;
using Xunit;

namespace Cya2.Database.Tests;

public sealed class DonationWorkbookReaderTests
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
    public void TributeHeaderIsRequiredButBlankRecipientValuesAreAllowed()
    {
        using var valid = Workbook();
        Assert.True(new DonationWorkbookReader().Read(valid).IsValid);
        using var missing = Workbook(sheet => sheet.Cells[1, 21].Value = "Other");
        var result = new DonationWorkbookReader().Read(missing);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Column == "Tribute System Record ID");
    }

    [Fact]
    public void UnknownInternIdIsAcceptedWithoutAccountLookup()
    {
        using var workbook = Workbook(sheet => sheet.Cells[2, 21].Value = "000unknown");
        var result = new DonationWorkbookReader().Read(workbook);
        Assert.Equal("000unknown", Assert.Single(result.Donations).InternSourceId);
    }

    [Fact]
    public void DuplicateRecognizedHeaderIsRejected()
    {
        using var workbook = Workbook(sheet => sheet.Cells[1, 23].Value = "constituent id");
        var result = new DonationWorkbookReader().Read(workbook);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Message == "Duplicate column: Constituent ID");
    }

    [Fact]
    public void MalformedPopulatedRowRejectsWholeWorkbook()
    {
        using var workbook = Workbook(sheet => sheet.Cells[3, 5].Value = "not an amount");
        var result = new DonationWorkbookReader().Read(workbook);
        Assert.False(result.IsValid);
        Assert.Empty(result.Donations);
        Assert.Contains(result.Errors, error => error.RowNumber == 3);
    }

    [Fact]
    public void EmptyAndHeaderOnlyWorkbooksAreRejected()
    {
        using var emptyPackage = new ExcelPackage();
        emptyPackage.Workbook.Worksheets.Add("Empty");
        using var empty = new MemoryStream(emptyPackage.GetAsByteArray());
        Assert.False(new DonationWorkbookReader().Read(empty).IsValid);
        using var headers = Workbook(sheet => sheet.Cells[2, 1, 2, Headers.Length].Clear());
        Assert.False(new DonationWorkbookReader().Read(headers).IsValid);
    }

    [Fact]
    public void ComplexityLimitsRejectBeforeRowNormalization()
    {
        using var workbook = Workbook(sheet => sheet.Cells[10, 30].Value = "unexpected data");
        var result = new DonationWorkbookReader(maximumRows: 5).Read(workbook);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Message.Contains("complexity limits"));
    }

    [Fact]
    public void AlternateHonorNameHeaderRemainsSupported()
    {
        using var workbook = Workbook(sheet => sheet.Cells[1, 22].Value = "Honor/Memorial Sort Name");
        Assert.True(new DonationWorkbookReader().Read(workbook).IsValid);
    }

    private static MemoryStream Workbook(Action<ExcelWorksheet>? configure = null)
    {
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
        configure?.Invoke(sheet);
        return new MemoryStream(package.GetAsByteArray());
    }
}
