using Cya2.Core.Imports;
using Cya2.Core.Interfaces;
using Cya2.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Cya2.Database.Tests.MySqlFixture;

namespace Cya2.Database.Tests;

public sealed class DonationAccountAssociationTests(MySqlFixture mysql) : IClassFixture<MySqlFixture>
{
    [MySqlFact]
    public async Task AssociationsPreserveAllocationAndExcludeUnmatchedAndConflictingRows()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, @"
INSERT INTO Accounts (AccountId, Fund) VALUES (1, 'Primary'), (2, 'Other');
INSERT INTO SubAccounts (AccountId, SubFund, Kind) VALUES (1, 'Merged', 'Merged'), (1, 'Separate', 'Separate'), (1, 'Conflict', 'Merged'), (2, 'Conflict', 'Separate');
INSERT INTO Accounts (AccountId, Fund, AccountKind, InternSourceId) VALUES (3, 'Opaque', 'Intern', '000Intern');
INSERT INTO Donors (Id, IdentityKey, DisplayName, ResolutionSource, ResolutionVersion) VALUES (1, 'id:1', 'Synthetic', 'SourceId', 2);
INSERT INTO DonationData (DonorId, Date, AccountName, PaymentMethod, GiftType, Amount, Fund, InternSourceId) VALUES
(1, '2025-01-01', '', '', '', 1, 'Primary', NULL), (1, '2025-01-01', '', '', '', 2, 'Merged', NULL),
(1, '2025-01-01', '', '', '', 3, 'Separate', NULL), (1, '2025-01-01', '', '', '', 4, 'Conflict', NULL),
(1, '2025-01-01', '', '', '', 5, 'Unknown', NULL), (1, '2025-01-01', '', '', '', 6, 'Primary', '000Intern'),
(1, '2025-01-01', '', '', '', 7, 'Primary', '000intern');");
        var configuration = Configuration(db.RuntimeConnection);
        var reads = new DonationReadRepository(configuration, new Available(), NullLogger<DonationReadRepository>.Instance);
        var start = new DateTime(2025, 1, 1);
        var end = start.AddYears(1);
        var primary = await reads.GetDonationsByAccountAndDateRangeAsync(1, "Primary", start, end);
        Assert.Equal(new[] { "Merged", "Primary" }, primary.Select(row => row.Fund).Order().ToArray());
        Assert.All(primary, row => Assert.Equal(1, row.AccountId));
        Assert.Equal("Separate", Assert.Single(await reads.GetDonationsByAccountAndDateRangeAsync(1, "Separate", start, end)).Fund);
        Assert.Equal(3, (await reads.GetDonationsForAccountScopeAsync(1, "Primary", start, end)).Count);
        Assert.Empty(await reads.GetDonationsByAccountAndDateRangeAsync(2, "Primary", start, end));
        Assert.Empty(await reads.GetDonationsByFundNamesAndDateRangeAsync(["Conflict", "Unknown"], start, end));
        Assert.Equal(3, Assert.Single(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000Intern", start, end)).AccountId);
        Assert.Empty(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000intern", start, end));
        var counts = await new DonationAccountMappingReader(configuration, new Available()).GetCountsAsync([
            Gift("Primary"), Gift("Unknown"), Gift("Conflict"), Gift("Primary", "000Intern"), Gift("Primary", "000intern")]);
        Assert.Equal(2, counts.UnresolvedNormalDonations);
        Assert.Equal(1, counts.UnresolvedInternDonations);
        await ExecuteAsync(db.SetupConnection, "DELETE FROM Accounts WHERE AccountId=3");
        Assert.Empty(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000Intern", start, end));
        Assert.Equal("7", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Accounts (AccountId, Fund, AccountKind, InternSourceId) VALUES (4, 'Recreated', 'Intern', '000Intern')");
        Assert.Equal(4, Assert.Single(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000Intern", start, end)).AccountId);
    }

    private static NormalizedDonation Gift(string fund, string? intern = null) => new()
    {
        SourceRows = [2], Date = new DateTime(2025, 1, 1), Amount = 1, Fund = fund,
        InternSourceId = intern, AccountName = "", DonorDisplayName = "Synthetic", PaymentMethod = "", GiftType = ""
    };

    private static IConfiguration Configuration(string connection) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:default"] = connection }).Build();

    private sealed class Available : IDatabaseGuard
    {
        public bool IsAvailable => true;
        public void ThrowIfUnavailable() { }
    }
}
