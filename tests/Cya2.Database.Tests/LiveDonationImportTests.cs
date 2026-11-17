using Cya2.Application.Interfaces;
using Cya2.Core.Imports;
using Cya2.Core.DTOs;
using Cya2.Core.Interfaces;
using Cya2.Infrastructure.Repositories;
using Cya2.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;
using static Cya2.Database.Tests.MySqlFixture;

namespace Cya2.Database.Tests;

public sealed class LiveDonationImportTests(MySqlFixture mysql) : IClassFixture<MySqlFixture>
{
    [MySqlFact]
    public async Task ReplacementProjectsSurvivingEvidenceAndRollbackRestoresIt()
    {
        var db = await mysql.CreateDatabaseAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:default"] = db.RuntimeConnection }).Build();
        var repository = new DonationRepository(config, new Available(), NullLogger<DonationRepository>.Instance);
        var older = Gift(new DateTime(2024, 1, 1), "older@example.invalid");
        var newer = Gift(new DateTime(2025, 1, 1), "newer@example.invalid");
        await repository.ReplaceCanonicalDonationsAsync([older, newer], default);
        Assert.Equal("newer@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts WHERE ContactType='Email'"));
        await repository.ReplaceCanonicalDonationsAsync([Gift(new DateTime(2025, 1, 1), null)], default);
        Assert.Equal("older@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts WHERE ContactType='Email'"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE SourceEvidenceJson IS NOT NULL AND InternSourceId='000intern'"));
        var rollback = new RollbackExecutor(config, NullLogger<RollbackExecutor>.Instance,
            new RollbackRepository(config, NullLogger<RollbackRepository>.Instance, new Available()));
        var restored = await rollback.RollbackDonationsAsync();
        Assert.True(restored.Success, restored.ErrorMessage);
        Assert.Equal("newer@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts WHERE ContactType='Email'"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE SourceEvidenceJson IS NOT NULL"));
    }

    [MySqlFact]
    public async Task NonLoginInternLifecycleReplacementAndRollbackRestoresOriginalInternSource()
    {
        var db = await mysql.CreateDatabaseAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:default"] = db.RuntimeConnection }).Build();
        var repository = new DonationRepository(config, new Available(), NullLogger<DonationRepository>.Instance);
        await repository.ReplaceCanonicalDonationsAsync([Gift(new DateTime(2025, 2, 1), null, "000intern-a")], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE InternSourceId='000intern-a'"));
        await repository.ReplaceCanonicalDonationsAsync([Gift(new DateTime(2025, 3, 1), null, "000intern-b")], default);
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE InternSourceId='000intern-a'"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE InternSourceId='000intern-b'"));
        var rollback = new RollbackExecutor(config, NullLogger<RollbackExecutor>.Instance,
            new RollbackRepository(config, NullLogger<RollbackRepository>.Instance, new Available()));
        var restored = await rollback.RollbackDonationsAsync();
        Assert.True(restored.Success, restored.ErrorMessage);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE InternSourceId='000intern-a'"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE InternSourceId='000intern-b'"));
    }

    [MySqlFact]
    public async Task InternAccountLifecycleRetainsTransactionsAndUsesSourceId()
    {
        var db = await mysql.CreateDatabaseAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:default"] = db.RuntimeConnection }).Build();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id, Email, Name, AuthLevel) VALUES (1, 'admin@example.invalid', 'Admin', 'Admin'), (2, 'viewer@example.invalid', 'User', 'User')");
        var repository = new DonationRepository(config, new Available(), NullLogger<DonationRepository>.Instance);
        await repository.ReplaceCanonicalDonationsAsync([Gift(new DateTime(2025, 1, 1), null)], default);
        var store = new InternAccountStore(config, new Available(), NullLogger<InternAccountStore>.Instance);
        var account = await store.SaveAsync(1, null, "Synthetic Intern", "000intern");
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Accounts WHERE AccountKind='Intern' AND AccountNumber IS NULL AND AccountingClass IS NULL"));
        await ExecuteAsync(db.SetupConnection, $"INSERT INTO AccountsUsers VALUES (2, {account.AccountId})");
        var reads = new DonationReadRepository(config, new Available(), NullLogger<DonationReadRepository>.Instance);
        Assert.Single(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000intern", new DateTime(2024, 1, 1), new DateTime(2026, 1, 1)));
        Assert.Empty(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000INTERN", new DateTime(2024, 1, 1), new DateTime(2026, 1, 1)));
        await store.SaveAsync(1, account.AccountId, "Corrected Intern", "000intern");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.DeleteAsync(2, account.AccountId));
        await store.DeleteAsync(1, account.AccountId);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers"));
        await store.SaveAsync(1, null, "Recreated Intern", "000intern");
        Assert.Single(await reads.GetInternDonationsByInternNameAndDateRangeAsync("000intern", new DateTime(2024, 1, 1), new DateTime(2026, 1, 1)));
    }

    private static CanonicalDonationWriteModel Gift(DateTime date, string? email, string internSourceId = "000intern")
    {
        var row = Assert.Single(new DonationUploadNormalizer().Normalize([new DonationSourceRow
        {
            RowNumber = 2, Date = date, Amount = 10, Fund = "Synthetic", ConstituentId = "000donor",
            AccountName = "Synthetic Person", PaymentMethod = "Check", GiftType = "Donation", InternSourceId = internSourceId, Email = email
        }]).Donations);
        return new CanonicalDonationWriteModel
        {
            Donor = new DonorWriteModel { Fund = row.Fund, DisplayName = row.DonorDisplayName, IdentityKey = "id:000donor", ConstituentId = "000donor", ResolutionSource = "SourceId", ResolutionVersion = 2 },
            Date = row.Date, Amount = row.Amount, Fund = row.Fund, AccountName = row.AccountName,
            PaymentMethod = row.PaymentMethod, GiftType = row.GiftType, ConstituentId = row.ConstituentId,
            InternSourceId = row.InternSourceId, SourceEvidenceJson = JsonSerializer.Serialize(row)
        };
    }

    private sealed class Available : IDatabaseGuard
    {
        public bool IsAvailable => true;
        public void ThrowIfUnavailable() { }
    }
}
