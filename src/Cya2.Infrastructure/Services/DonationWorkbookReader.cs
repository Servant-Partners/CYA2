using System.Collections.Immutable;
using System.Globalization;
using Cya2.Core.Imports;
using OfficeOpenXml;

namespace Cya2.Infrastructure.Services;

public sealed class DonationWorkbookReader : Cya2.Application.Interfaces.IDonationWorkbookReader
{
    private static readonly string[] RequiredHeaders =
    [
        "Gift Date", "Name", "Gift Payment Type", "Gift Type", "Fund Split Amount", "Fund Notes",
        "Soft Credit Recipient Name", "Preferred Address Line 1", "Preferred City", "Preferred State",
        "Preferred ZIP", "Preferred Country", "Personal Email Number", "Home Phone Number",
        "Personal Mobile Phone Number", "Gift Is Anonymous", "Gift Import ID", "Primary Addressee",
        "Constituent ID", "Soft Credit Recipient ID", "Tribute System Record ID"
    ];
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly int _maximumRows;
    private readonly int _maximumColumns;
    private readonly long _maximumCells;

    public DonationWorkbookReader(int maximumRows = 250_000, int maximumColumns = 256, long maximumCells = 10_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumColumns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCells, 1);
        _maximumRows = maximumRows;
        _maximumColumns = maximumColumns;
        _maximumCells = maximumCells;
    }

    public DonationUploadValidationResult Read(Stream file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        using var package = new ExcelPackage(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (package.Workbook.Worksheets.Count == 0) return Reject("No worksheet found.");
        var sheet = package.Workbook.Worksheets[0];
        var dimension = sheet.Dimension;
        if (dimension is null) return Reject("No donation rows found. Existing data was not changed.");
        if (dimension.End.Row > _maximumRows || dimension.End.Column > _maximumColumns
            || (long)dimension.End.Row * dimension.End.Column > _maximumCells)
            return Reject("The worksheet exceeds the configured donation import complexity limits.");

        var errors = ImmutableArray.CreateBuilder<DonationUploadError>();
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var known = RequiredHeaders.Concat(["Honor/Memorial Sort Name", "Honor/Memorial Name"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var column = 1; column <= dimension.End.Column; column++)
        {
            var header = sheet.Cells[1, column].Text.Trim();
            if (!known.Contains(header)) continue;
            if (!columns.TryAdd(header, column))
            {
                var expected = known.First(value => string.Equals(value, header, StringComparison.OrdinalIgnoreCase));
                errors.Add(new DonationUploadError(1, expected, $"Duplicate column: {expected}"));
            }
        }
        foreach (var header in RequiredHeaders)
            if (!columns.ContainsKey(header)) errors.Add(new DonationUploadError(1, header, $"Missing column: {header}"));
        var honorHeader = columns.ContainsKey("Honor/Memorial Sort Name") ? "Honor/Memorial Sort Name" : "Honor/Memorial Name";
        if (!columns.ContainsKey(honorHeader))
            errors.Add(new DonationUploadError(1, "Honor/Memorial Name", "Missing column: Honor/Memorial Sort Name or Honor/Memorial Name"));
        if (errors.Count != 0) return new DonationUploadValidationResult { Donations = [], Errors = errors.ToImmutable() };

        return new DonationUploadNormalizer().Normalize(Rows(), cancellationToken);

        IEnumerable<DonationSourceRow> Rows()
        {
            for (var row = 2; row <= dimension.End.Row; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blank = true;
                for (var column = 1; column <= dimension.End.Column; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(sheet.Cells[row, column].Text)) { blank = false; break; }
                }
                if (blank) continue;
                DateTime? date = ExcelParsingHelpers.TryParseDateUS(Cell("Gift Date"), out var parsedDate) ? parsedDate : null;
                decimal? amount = decimal.TryParse(Cell("Fund Split Amount"), NumberStyles.Any, UsCulture, out var parsedAmount) ? parsedAmount : null;
                yield return new DonationSourceRow
                {
                    RowNumber = row,
                    Date = date,
                    Amount = amount,
                    Fund = Cell("Fund Notes"),
                    GiftImportId = Cell("Gift Import ID"),
                    AccountName = Cell("Name"),
                    PrimaryAddressee = Cell("Primary Addressee"),
                    SoftCreditName = Cell("Soft Credit Recipient Name"),
                    ConstituentId = Cell("Constituent ID"),
                    SoftCreditRecipientId = Cell("Soft Credit Recipient ID"),
                    InternSourceId = Cell("Tribute System Record ID"),
                    InternDisplayName = Cell(honorHeader),
                    PaymentMethod = Cell("Gift Payment Type"),
                    GiftType = Cell("Gift Type"),
                    IsAnonymous = ExcelParsingHelpers.ParseYesNo(Cell("Gift Is Anonymous")),
                    Address = new DonationAddress(Cell("Preferred Address Line 1"), Cell("Preferred City"), Cell("Preferred State"), Cell("Preferred ZIP"), Cell("Preferred Country")),
                    Email = Cell("Personal Email Number"),
                    HomePhone = Cell("Home Phone Number"),
                    MobilePhone = Cell("Personal Mobile Phone Number")
                };

                string Cell(string header) => sheet.Cells[row, columns[header]].Text.Trim();
            }
        }
    }

    private static DonationUploadValidationResult Reject(string message) => new()
    {
        Donations = [],
        Errors = [new DonationUploadError(null, string.Empty, message)]
    };
}
