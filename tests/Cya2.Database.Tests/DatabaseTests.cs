using Cya2.Core.DTOs;
using Cya2.Core.Interfaces;
using Cya2.Infrastructure.Repositories;
using Cya2.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Xunit;
using Xunit.Abstractions;
using Cya2.Core.Entities;
using Cya2.Application.Interfaces;
using Cya2.Core.Imports;
using System.Text.Json;
using static Cya2.Database.Tests.MySqlFixture;

namespace Cya2.Database.Tests;

public sealed class DatabaseTests(MySqlFixture mysql, ITestOutputHelper output) : IClassFixture<MySqlFixture>
{
    private static readonly DateTime Date = new(2026, 1, 15);
    private static readonly AvailableDatabase Guard = new();

    [MySqlFact]
    public async Task AccountingReadsWithNullClassUseAccountNumberWithoutMatchingEmptyClasses()
    {
        var db = await mysql.CreateDatabaseAsync();
        var importer = new AccountingImportRepository(Config(db), NullLogger<AccountingImportRepository>.Instance, new NoProgress(), Guard);
        var matching = new AccountingImportRowDto { AccountingClass = "Other Class", Date = Date, Num = "A", Amount = 10, AccountNumber = "1000", Account = "Test", Type = "Deposit" };
        var emptyClass = new AccountingImportRowDto { AccountingClass = "", Date = Date, Num = "B", Amount = 20, AccountNumber = "2000", Account = "Test", Type = "Deposit" };
        await importer.ReplaceAsync([matching, emptyClass], default);

        var reader = new ExpenseReadRepository(Config(db), Guard, NullLogger<ExpenseReadRepository>.Instance);
        var single = await reader.GetAccountingDataForAccountAsync(null, "1000", Date.AddDays(-1), Date.AddDays(1));
        var batch = await reader.GetAccountingDataForAccountsAsync([(42, null, "1000"), (43, null, "2000")], Date.AddDays(-1), Date.AddDays(1));

        Assert.Equal(10, Assert.Single(single).Amount);
        Assert.Equal(10, Assert.Single(batch[42]).Amount);
        Assert.Equal(20, Assert.Single(batch[43]).Amount);
    }

    [MySqlFact]
    public async Task SoftRecipientIdLinksDirectAcrossFundsAndSurvivesRelinkAndRollback()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        CanonicalDonationWriteModel Gift(bool soft, DateTime date, string id, string fund) =>
            SourceDonation(id, soft ? "Soft Person" : "Direct Person", soft, date, fund,
                [Email(soft ? "employer@example.invalid" : "donor@example.invalid")]);
        await repository.ReplaceCanonicalDonationsAsync([Gift(true, Date.AddMonths(-2), "000123", "Separate Fund")], default);
        await repository.ReplaceCanonicalDonationsAsync([Gift(false, Date, "000123", "Primary Fund")], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT Fund) FROM DonationData"));
        Assert.Equal("000123", await ScalarAsync(db.RuntimeConnection, "SELECT SoftCreditRecipientId FROM DonationData WHERE IsSoftCredit=1"));
        Assert.Equal("employer", await ScalarAsync(db.RuntimeConnection, "SELECT ConstituentId FROM DonationData WHERE IsSoftCredit=1"));
        Assert.Equal("donor@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
        var rollback = await Rollback(db).RollbackDonationsAsync();
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("000123", await ScalarAsync(db.RuntimeConnection, "SELECT SoftCreditRecipientId FROM DonationData"));
        Assert.Equal("000123", await ScalarAsync(db.RuntimeConnection, "SELECT ConstituentId FROM Donors"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts"));
    }

    [MySqlFact]
    public async Task DifferentSoftRecipientIdsRemainDistinctAndBlankIsRejected()
    {
        var db = await mysql.CreateDatabaseAsync();
        var invalid = new DonationUploadNormalizer().Normalize([new DonationSourceRow
        {
            RowNumber = 2, Date = Date, Fund = "Test Fund", Amount = 10,
            AccountName = "Employer", PrimaryAddressee = "Employer", SoftCreditName = "Matched",
            ConstituentId = "employer", PaymentMethod = "Check", GiftType = "Donation"
        }]);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Column == "Soft Credit Recipient ID");
        await Donations(db).ReplaceCanonicalDonationsAsync([
            SourceDonation("direct-id", "Matched"),
            SourceDonation("direct-id", "Matched", soft: true),
            SourceDonation("other-id", "Different", soft: true)], default);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData d JOIN Donors p ON d.DonorId=p.Id WHERE p.ConstituentId='direct-id'"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Donors WHERE ConstituentId='other-id'"));
    }

    [MySqlFact]
    public async Task RequiredAuditFailureRollsBackImportsAndRollbackPublication()
    {
        var db = await mysql.CreateDatabaseAsync();
        var donations = Donations(db);
        var accounting = new AccountingImportRepository(Config(db), NullLogger<AccountingImportRepository>.Instance, new NoProgress(), Guard);
        await donations.ReplaceCanonicalDonationsAsync([Donation(amount: 10)], default);
        await donations.ReplaceCanonicalDonationsAsync([Donation(amount: 20)], default);
        await accounting.ReplaceAsync([Accounting(Date, 10)], default);
        await accounting.ReplaceAsync([Accounting(Date, 20)], default);
        await ExecuteAsync(db.SetupConnection, "CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected audit failure'");

        await Assert.ThrowsAsync<AuditPersistenceException>(() => donations.ReplaceCanonicalDonationsAsync([Donation(amount: 99)], default));
        await Assert.ThrowsAsync<AuditPersistenceException>(() => accounting.ReplaceAsync([Accounting(Date, 99)], default));
        Assert.False((await Rollback(db).RollbackDonationsAsync()).Success);
        Assert.False((await Rollback(db).RollbackAccountingAsync()).Success);

        Assert.Equal("20.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM DonationData"));
        Assert.Equal("20.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM AccountingData"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationBackupSnapshots WHERE Pinned=1"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountingBackupSnapshots WHERE Pinned=1"));
    }

    [MySqlFact]
    public async Task RequiredAuditFailureRollsBackAdministrationAndBootstrap()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected audit failure'");
        var users = new UserRepository(Config(db), Guard, NullLogger<UserRepository>.Instance);
        await Assert.ThrowsAsync<AuditPersistenceException>(() => users.TryCreateInitialAdminAsync(new User { Email = "first@scene8.net", Name = "First", AuthLevel = "Admin", Language = "en-US" }));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'),(2,'user@scene8.net','Original','User')");
        var store = new UserAdministrationStore(Config(db), Guard);
        await Assert.ThrowsAsync<AuditPersistenceException>(() => store.CreateAsync(new(1, "New", "new@scene8.net", "User", [], false)));
        await Assert.ThrowsAsync<AuditPersistenceException>(() => store.UpdateAsync(new(1, 2, "Changed", null, null, null, null, false)));
        await Assert.ThrowsAsync<AuditPersistenceException>(() => store.DeleteAsync(1, 2));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("Original", await ScalarAsync(db.RuntimeConnection, "SELECT Name FROM Users WHERE Id=2"));
    }

    [MySqlFact]
    public async Task TransactionalSuccessSharesStartIdentityAndNumericReferencesSurviveDeletion()
    {
        var db = await mysql.CreateDatabaseAsync();
        var audit = new AuditEventRepository(Config(db), Guard);
        using (var operation = AuditOperationScope.Begin(1))
        {
            await audit.AddAsync(AuditOperationScope.CreateEvent(1, AuditEventTypes.DonationImportStarted, AuditOutcomes.Started));
            await Donations(db).ReplaceCanonicalDonationsAsync([Donation()], default);
            Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, $"SELECT COUNT(*) FROM AuditEvents WHERE OperationId='{operation.OperationId}' AND ActorUserId=1"));
        }
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'one@scene8.net','One','Admin'),(2,'two@scene8.net','Two','Admin')");
        var store = new UserAdministrationStore(Config(db), Guard);
        Assert.Equal(UserAdministrationStatus.Success, (await store.DeleteAsync(1, 2)).Status);
        await ExecuteAsync(db.SetupConnection, "DELETE FROM Users WHERE Id=1");
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AuditEvents WHERE EventType='AdminUserDeleted' AND ActorUserId=1 AND TargetUserId=2"));
    }

    [MySqlFact]
    public async Task RestartReconciliationIsIdempotentAndDoesNotFlagFinishedCurrentOrRecentWork()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = new AuditEventRepository(Config(db), Guard);
        var currentHost = AuditOperationScope.HostInstanceId;
        var interrupted = Guid.NewGuid().ToString("N");
        foreach (var kind in new[] { "interrupted", "complete", "current", "recent" })
        {
            var id = kind == "interrupted" ? interrupted : Guid.NewGuid().ToString("N");
            var start = new AuditEvent { OperationId = id, HostInstanceId = kind == "current" ? currentHost : Guid.NewGuid().ToString("N"), EventType = AuditEventTypes.DonationImportStarted, Outcome = AuditOutcomes.Started, OccurredAtUtc = kind == "recent" ? DateTime.UtcNow : DateTime.UtcNow.AddHours(-1) };
            await repository.AddAsync(start);
            await repository.AddAsync(start);
            if (kind == "complete")
                await repository.AddAsync(new AuditEvent { OperationId = id, EventType = AuditEventTypes.DonationImportCompleted, Outcome = AuditOutcomes.Succeeded, OccurredAtUtc = DateTime.UtcNow });
        }
        Assert.Equal(1, await repository.FlagInterruptedImportsAsync(currentHost, DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(0, await repository.FlagInterruptedImportsAsync(currentHost, DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(interrupted, await ScalarAsync(db.RuntimeConnection, "SELECT OperationId FROM AuditEvents WHERE Outcome='NeedsReconciliation'"));
        Assert.Equal("6", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AuditEvents"));
    }

    [MySqlFact]
    public async Task AuditRetentionDeletesOnlyRecordsOlderThanCutoff()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = new AuditEventRepository(Config(db), Guard);
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var time in new[] { cutoff.AddSeconds(-1), cutoff, cutoff.AddSeconds(1) })
            await repository.AddAsync(new AuditEvent { EventType = AuditEventTypes.DonorExportCompleted, Outcome = AuditOutcomes.Succeeded, OccurredAtUtc = time });
        Assert.Equal(1, await repository.DeleteExpiredAsync(cutoff));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AuditEvents"));
    }

    [MySqlFact]
    public async Task InternDonationReads_MatchExactSourceIdsAndAgreeWithBulk()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin')");
        var store = new InternAccountStore(Config(db), Guard, NullLogger<InternAccountStore>.Instance);
        var names = new[] { "000intern", "000intern-extra", "000INTERN", "000intern-e", "000intern-é" };
        foreach (var sourceId in names)
            await store.SaveAsync(1, null, "Synthetic Intern", sourceId);
        await Donations(db).ReplaceCanonicalDonationsAsync(names.Select(sourceId =>
            SourceDonation("read-donor", "Synthetic Donor", fund: "Shared", internSourceId: sourceId,
                internDisplayName: "Synthetic Intern")).Append(
            SourceDonation("read-donor", "Synthetic Donor", fund: "Other", internSourceId: names[0],
                internDisplayName: "Synthetic Intern")).ToArray(), default);
        var reader = new DonationReadRepository(Config(db), Guard, NullLogger<DonationReadRepository>.Instance);
        var bulk = await reader.GetInternDonationsByNamesAndDateRangeAsync(names, Date.AddDays(-1), Date.AddDays(1));

        foreach (var name in names)
        {
            var single = await reader.GetInternDonationsByInternNameAndDateRangeAsync(name, Date.AddDays(-1), Date.AddDays(1));
            Assert.Equal(single.Select(row => row.Id).Order(), bulk[name].Select(row => row.Id).Order());
            Assert.All(single, row => Assert.Equal(name, row.InternName));
        }
        Assert.Equal(2, bulk[names[0]].Count);
        Assert.All(names.Skip(1), name => Assert.Single(bulk[name]));
        Assert.Empty(await reader.GetInternDonationsByInternNameAndDateRangeAsync("unconfigured-id", Date.AddDays(-1), Date.AddDays(1)));
    }

    [MySqlFact]
    public async Task StableSourceId_PreservesDirectContactTimestamps_AndRollbackRestoresOriginalIdentity()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        CanonicalDonationWriteModel Gift(string name, DateTime date) =>
            SourceDonation("000123", name, date: date, contacts: [Email("alex@example.invalid")]);
        await repository.ReplaceCanonicalDonationsAsync([Gift("Alex Smith", Date.AddMonths(-2))], default);
        await ExecuteAsync(db.SetupConnection, "UPDATE DonorContacts SET DateCreated='2020-01-01', DateModified='2021-01-01'");
        var original = await ScalarAsync(db.RuntimeConnection, "SELECT DonorId FROM DonationData");
        await repository.ReplaceCanonicalDonationsAsync([Gift("Smith, Alex", Date)], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Donors"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE DateCreated='2020-01-01' AND DateModified='2021-01-01'"));
        var rollback = await Rollback(db).RollbackDonationsAsync();
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal(original, await ScalarAsync(db.RuntimeConnection, "SELECT DonorId FROM DonationData"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE DateCreated='2020-01-01' AND DateModified='2021-01-01'"));
    }

    [MySqlFact]
    public async Task DistinctSourceIds_KeepRetainedSoftCreditsSeparateWithoutCopyingContacts_AndRollbackRestoresThem()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        CanonicalDonationWriteModel Gift(string id, bool soft, DateTime date) =>
            SourceDonation(id, "Same Name", soft, date, contacts: [Email(id + "@example.invalid")]);
        await repository.ReplaceCanonicalDonationsAsync([
            Gift("1", false, Date.AddMonths(-2)), Gift("3", true, Date.AddMonths(-1))], default);
        await repository.ReplaceCanonicalDonationsAsync([Gift("2", false, Date)], default);
        Assert.Equal("3", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts c JOIN Donors d ON d.Id=c.DonorId WHERE d.ConstituentId='3'"));
        Assert.Equal("employer", await ScalarAsync(db.RuntimeConnection, "SELECT ConstituentId FROM DonationData WHERE IsSoftCredit=1"));
        var rollback = await Rollback(db).RollbackDonationsAsync();
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
        Assert.Equal("1@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
    }

    [MySqlFact]
    public async Task SharedConstituentIdentity_CombinesFunds_IgnoresOrganizationIds_AndRollsBackRelinks()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        CanonicalDonationWriteModel Gift(string name, string id, string fund, bool soft = false, DateTime? date = null, string? email = null) =>
            SourceDonation(id, name, soft, date, fund, email == null ? [] : [Email(email)]);
        await repository.ReplaceCanonicalDonationsAsync([Gift("Alex Smith", "000123", "A", true, Date.AddMonths(-1))], default);
        var originalId = await ScalarAsync(db.RuntimeConnection, "SELECT DonorId FROM DonationData");
        await repository.ReplaceCanonicalDonationsAsync([
            Gift("Alex Smith", "000123", "B", email: "alex@example.invalid"),
            Gift("Smith, Alex", "000123", "C", true, email: "employer@example.invalid"),
            Gift("Other Person", "000456", "C", true)
        ], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData WHERE Fund IN ('A','B')"));
        Assert.Equal("3", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData d JOIN Donors n ON n.Id=d.DonorId WHERE n.ConstituentId='000123'"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Donors WHERE ConstituentId='employer'"));
        Assert.Equal("alex@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
        var rollback = await Rollback(db).RollbackDonationsAsync();
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        Assert.Equal(originalId, await ScalarAsync(db.RuntimeConnection, "SELECT DonorId FROM DonationData"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts"));
    }

    [MySqlFact]
    public async Task SharedIdentity_SourceIdsDistinguishSameNames_AndSoftOnlyUploadKeepsDirectContacts()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        CanonicalDonationWriteModel Gift(string id, bool soft, DateTime date) =>
            SourceDonation(id, "Same Name", soft, date, contacts: [Email("direct@example.invalid")]);
        await repository.ReplaceCanonicalDonationsAsync([Gift("1", false, Date.AddMonths(-1)), Gift("1", true, Date)], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
        await repository.ReplaceCanonicalDonationsAsync([Gift("1", true, Date)], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts"));
        await repository.ReplaceCanonicalDonationsAsync([Gift("2", false, Date), Gift("1", true, Date)], default);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT DonorId) FROM DonationData"));
    }

    [MySqlFact]
    public async Task FreshSchema_InitializesWithoutHistory_AndRejectsRerunsWithoutChangingData()
    {
        var db = await mysql.CreateDatabaseAsync();
        Assert.Equal("0", await ScalarAsync(db.SetupConnection, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='Cya2SchemaMigrations'"));
        Assert.Equal("2", await ScalarAsync(db.SetupConnection, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name IN ('DonationBackupSnapshots', 'AccountingBackupSnapshots') AND column_name='BackupAt' AND datetime_precision=6"));
        Assert.Equal("6", await ScalarAsync(db.SetupConnection, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name IN ('DonationData', 'DonationDataBackup') AND column_name IN ('ConstituentId', 'SoftCreditRecipientId', 'IsSoftCredit')"));
        Assert.Equal("1", await ScalarAsync(db.SetupConnection, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='AuditEvents'"));
        await Donations(db).ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("original@example.invalid")])], default);

        var rerun = await MigrateAsync(db.SetupConnection);
        Assert.True(rerun.ExitCode == 1, rerun.Output);
        Assert.Contains("requires --initialize", rerun.Output, StringComparison.OrdinalIgnoreCase);

        var reinitialize = await MigrateAsync(db.SetupConnection, initialize: true);
        Assert.True(reinitialize.ExitCode == 1, reinitialize.Output);
        Assert.Contains("requires an empty database", reinitialize.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        Assert.Equal("10.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM DonationData"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Donors"));
        Assert.Equal("original@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationBackupSnapshots"));
        Assert.Equal("0", await ScalarAsync(db.SetupConnection, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='Cya2SchemaMigrations'"));
    }

    [MySqlFact]
    public async Task ContactFreshness_PreservesUnchangedTimestamps_TracksExactChanges_AndRestoresOnRollback()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("keep@example.invalid"), Email("old@example.invalid")])], default);
        await ExecuteAsync(db.SetupConnection, "UPDATE DonorContacts SET DateCreated='2020-01-01 00:00:00', DateModified='2021-01-01 00:00:00'");
        var created = await ScalarAsync(db.RuntimeConnection, "SELECT DateCreated FROM DonorContacts WHERE ContactValue='keep@example.invalid'");
        var modified = await ScalarAsync(db.RuntimeConnection, "SELECT DateModified FROM DonorContacts WHERE ContactValue='keep@example.invalid'");
        var id = await ScalarAsync(db.RuntimeConnection, "SELECT Id FROM DonorContacts WHERE ContactValue='keep@example.invalid'");

        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("keep@example.invalid"), Email("old@example.invalid")])], default);
        Assert.Equal(created, await ScalarAsync(db.RuntimeConnection, "SELECT DateCreated FROM DonorContacts WHERE ContactValue='keep@example.invalid'"));
        Assert.Equal(modified, await ScalarAsync(db.RuntimeConnection, "SELECT DateModified FROM DonorContacts WHERE ContactValue='keep@example.invalid'"));

        var changed = new DonorContactWriteModel { ContactType = "Email", ContactValue = "KEEP@example.invalid", NormalizedValue = "keep@example.invalid" };
        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [changed, Email("new@example.invalid")])], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE BINARY ContactValue='KEEP@example.invalid' AND DateModified > '2021-01-01 00:00:00'"));
        Assert.Equal(id, await ScalarAsync(db.RuntimeConnection, "SELECT Id FROM DonorContacts WHERE NormalizedValue='keep@example.invalid'"));
        Assert.Equal(created, await ScalarAsync(db.RuntimeConnection, "SELECT DateCreated FROM DonorContacts WHERE NormalizedValue='keep@example.invalid'"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='new@example.invalid' AND DateCreated IS NOT NULL AND DateModified IS NULL"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='old@example.invalid'"));

        var changedAt = await ScalarAsync(db.RuntimeConnection, "SELECT DateModified FROM DonorContacts WHERE NormalizedValue='keep@example.invalid'");
        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [changed, Email("new@example.invalid")])], default);
        Assert.Equal(changedAt, await ScalarAsync(db.RuntimeConnection, "SELECT DateModified FROM DonorContacts WHERE NormalizedValue='keep@example.invalid'"));

        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("keep@example.invalid"), Email("old@example.invalid")])], default);
        var result = await Rollback(db).RollbackDonationsAsync();
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(changedAt, await ScalarAsync(db.RuntimeConnection, "SELECT DateModified FROM DonorContacts WHERE BINARY ContactValue='KEEP@example.invalid'"));
        Assert.Equal(created, await ScalarAsync(db.RuntimeConnection, "SELECT DateCreated FROM DonorContacts WHERE NormalizedValue='keep@example.invalid'"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='new@example.invalid' AND DateModified IS NULL"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='old@example.invalid'"));
    }

    [MySqlFact]
    public async Task ContactFreshness_EmptyContactSetClearsImportedDonorOnly()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        await repository.ReplaceCanonicalDonationsAsync([
            Donation(contacts: [Email("included@example.invalid")]),
            Donation(identity: "unrelated", date: Date.AddMonths(-1), contacts: [Email("untouched@example.invalid")])
        ], default);
        await repository.ReplaceCanonicalDonationsAsync([Donation()], default);
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='included@example.invalid'"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='untouched@example.invalid' AND DateModified IS NULL"));
    }

    [MySqlFact]
    public async Task RuntimeIdentity_CanUseTemporaryTables_ButCannotManagePersistentSchema()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.RuntimeConnection, "CREATE TEMPORARY TABLE TestStage (Id INT); INSERT INTO TestStage VALUES (1); DROP TEMPORARY TABLE TestStage");
        foreach (var sql in new[]
        {
            "CREATE TABLE ForbiddenTable (Id INT)",
            "ALTER TABLE DonationData ADD ForbiddenColumn INT",
            "DROP TABLE DonationData",
            "GRANT ALL ON *.* TO CURRENT_USER()"
        })
        {
            var error = await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(db.RuntimeConnection, sql));
            Assert.Contains(error.Number, new[] { 1044, 1045, 1142, 1227 });
        }
        var result = await Donations(db).ReplaceCanonicalDonationsAsync([Donation()], default);
        Assert.Equal(1, result.Inserted);
    }

    [MySqlFact]
    public async Task DonationPersistence_ReconcilesCurrentContacts_PreservingRetainedContactCreation()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Accounts (Fund,AccountNumber) VALUES ('Test Fund','1000')");
        var repository = Donations(db);
        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("keep@example.invalid"), Email("old@example.invalid")])], default);
        var created = await ScalarAsync(db.RuntimeConnection, "SELECT DateCreated FROM DonorContacts WHERE ContactValue='keep@example.invalid'");
        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("keep@example.invalid"), Email("new@example.invalid")])], default);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Donors"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='old@example.invalid'"));
        Assert.Equal(created, await ScalarAsync(db.RuntimeConnection, "SELECT DateCreated FROM DonorContacts WHERE ContactValue='keep@example.invalid'"));
        var reader = new DonationReadRepository(Config(db), Guard, NullLogger<DonationReadRepository>.Instance);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonorContacts WHERE ContactValue='keep@example.invalid' AND DateModified IS NULL"));
        var rows = await reader.GetDonationsByFundsAndDateRangeAsync(["Test Fund"], Date.AddDays(-1), Date.AddDays(1));
        Assert.Single(rows);
    }

    [MySqlFact]
    public async Task ContactWriteFailure_RollsBackDonorDonationAndContactChanges()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "CREATE TRIGGER FailContact BEFORE INSERT ON DonorContacts FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected integration test failure'");
        var error = await Assert.ThrowsAsync<MySqlException>(() => Donations(db).ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("test@example.invalid")])], default));
        Assert.Equal(1644, error.Number);
        foreach (var table in new[] { "Donors", "DonationData", "DonorContacts" })
            Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, $"SELECT COUNT(*) FROM {table}"));
    }

    [MySqlFact]
    public async Task DonationRollback_RestoresSnapshots_AndRemovesNewOrphanDonors()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = Donations(db);
        await repository.ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("original@example.invalid")])], default);
        await repository.ReplaceCanonicalDonationsAsync([
            Donation(amount: 99, contacts: [Email("replacement@example.invalid")]),
            Donation(identity: "newdonor", amount: 25)
        ], default);
        var result = await Rollback(db).RollbackDonationsAsync();
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        Assert.Equal("10.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM DonationData"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Donors"));
        Assert.Equal("original@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationBackupSnapshots WHERE Pinned=1"));
    }

    [MySqlFact]
    public async Task AccountingImportAndRollback_RestoreOriginalRows_IncludingEarlierRange()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = new AccountingImportRepository(Config(db), NullLogger<AccountingImportRepository>.Instance, new NoProgress(), Guard);
        var initial = await repository.ReplaceAsync([Accounting(Date.AddMonths(-1), 5), Accounting(Date, 10)], default);
        Assert.Equal(2, initial.Inserted);
        Assert.Empty(initial.Errors);
        var replacement = await repository.ReplaceAsync([Accounting(Date, 99)], default);
        Assert.Equal(1, replacement.Inserted);
        Assert.Empty(replacement.Errors);
        var result = await Rollback(db).RollbackAccountingAsync();
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountingData"));
        Assert.Equal("15.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(SUM(Amount) AS CHAR) FROM AccountingData"));
    }

    [MySqlFact]
    public async Task RollbackWithoutSnapshot_FailsWithoutChangingData()
    {
        var db = await mysql.CreateDatabaseAsync();
        await Donations(db).ReplaceCanonicalDonationsAsync([Donation()], default);
        await ExecuteAsync(db.SetupConnection, "DELETE FROM DonationBackupSnapshots; DELETE FROM DonationDataBackup; DELETE FROM DonorContactsBackup; DELETE FROM DonorsBackup");
        var result = await Rollback(db).RollbackDonationsAsync();
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
    }

    [MySqlFact]
    public async Task FailedReplacementPreservesExistingDonationAndSnapshot()
    {
        var db = await mysql.CreateDatabaseAsync();
        await Donations(db).ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("original@example.invalid")])], default);
        var backup = await ScalarAsync(db.RuntimeConnection, "SELECT BackupId FROM DonationBackupSnapshots LIMIT 1");
        await ExecuteAsync(db.SetupConnection, "CREATE TRIGGER FailContact BEFORE INSERT ON DonorContacts FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure'");
        await Assert.ThrowsAsync<MySqlException>(() => Donations(db).ReplaceCanonicalDonationsAsync([Donation(amount: 99, contacts: [Email("changed@example.invalid")])], default));
        Assert.Equal("10.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM DonationData"));
        Assert.Equal("original@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
        Assert.Equal(backup, await ScalarAsync(db.RuntimeConnection, "SELECT BackupId FROM DonationBackupSnapshots LIMIT 1"));
    }

    [MySqlFact]
    public async Task RollbackRestoresTouchedDonorWithOnlyOlderHistory()
    {
        var db = await mysql.CreateDatabaseAsync();
        await Donations(db).ReplaceCanonicalDonationsAsync([Donation(date: Date.AddMonths(-2), contacts: [Email("original@example.invalid")])], default);
        await Donations(db).ReplaceCanonicalDonationsAsync([Donation(contacts: [Email("changed@example.invalid")])], default);
        var result = await Rollback(db).RollbackDonationsAsync();
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData"));
        Assert.Equal("original@example.invalid", await ScalarAsync(db.RuntimeConnection, "SELECT ContactValue FROM DonorContacts"));
    }

    [MySqlFact]
    public async Task AccountingPublicationFailureRollsBackDeletionAndSnapshot()
    {
        var db = await mysql.CreateDatabaseAsync();
        var repository = new AccountingImportRepository(Config(db), NullLogger<AccountingImportRepository>.Instance, new NoProgress(), Guard);
        await repository.ReplaceAsync([Accounting(Date, 10)], default);
        var backup = await ScalarAsync(db.RuntimeConnection, "SELECT BackupId FROM AccountingBackupSnapshots LIMIT 1");
        await ExecuteAsync(db.SetupConnection, "CREATE TRIGGER FailAccounting BEFORE INSERT ON AccountingData FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure'");
        await Assert.ThrowsAsync<MySqlException>(() => repository.ReplaceAsync([Accounting(Date, 99)], default));
        Assert.Equal("10.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM AccountingData"));
        Assert.Equal(backup, await ScalarAsync(db.RuntimeConnection, "SELECT BackupId FROM AccountingBackupSnapshots LIMIT 1"));
    }

    [MySqlFact]
    public async Task PreCancelledReplacementPreservesData()
    {
        var db = await mysql.CreateDatabaseAsync();
        await Donations(db).ReplaceCanonicalDonationsAsync([Donation()], default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Donations(db).ReplaceCanonicalDonationsAsync([Donation(amount: 99)], cancellation.Token));
        Assert.Equal("10.00", await ScalarAsync(db.RuntimeConnection, "SELECT CAST(Amount AS CHAR) FROM DonationData"));
    }

    [MySqlFact]
    public async Task UserBatch_AuditFailureRollsBackWholeBatch()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'); CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected audit failure'");
        var store = new UserAdministrationStore(Config(db), Guard);
        await Assert.ThrowsAsync<AuditPersistenceException>(() => store.CreateBatchAsync(1, [new("First User", "first.user@scene8.net", "User"), new("Second User", "second.user@scene8.net", "Admin")]));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AuditEvents"));
    }

    [MySqlFact]
    public async Task UserBatch_CreatesDistinctAuditRecordsAndNoAccountLinks()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin')");
        var store = new UserAdministrationStore(Config(db), Guard);
        var result = await store.CreateBatchAsync(1, [new("First User", "first.user@scene8.net", "User"), new("Second User", "second.user@scene8.net", "Viewer")]);
        Assert.Equal(UserAdministrationStatus.Success, result.Status);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT OperationId) FROM AuditEvents WHERE EventType='AdminUserCreated'"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(DISTINCT TargetUserId) FROM AuditEvents WHERE EventType='AdminUserCreated'"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers"));
    }

    [MySqlFact]
    public async Task UserBatch_DuplicateLaterRowRollsBackUsersAndAudits()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'),(2,'second.user@scene8.net','Existing','User')");
        var store = new UserAdministrationStore(Config(db), Guard);
        var result = await store.CreateBatchAsync(1, [new("First User", "first.user@scene8.net", "User"), new("Second User", "second.user@scene8.net", "Viewer")]);
        Assert.Equal(UserAdministrationStatus.DuplicateEmail, result.Status);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AuditEvents"));
        Assert.Equal(UserAdministrationStatus.NotAuthorized, (await store.CreateBatchAsync(2, [new("Third User", "third.user@scene8.net", "Admin")])).Status);
    }

    [MySqlFact]
    public async Task UserAdministration_PreventsSelfLockoutAndConcurrentAdminLoss()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'one@scene8.net','One','Admin'),(2,'two@scene8.net','Two','Admin')");
        var store = new UserAdministrationStore(Config(db), Guard);

        Assert.Equal(UserAdministrationStatus.SelfChange, (await store.DeleteAsync(1, 1)).Status);
        Assert.Equal(UserAdministrationStatus.SelfChange, (await store.UpdateAsync(new(1, 1, null, null, "User", null, null, false))).Status);
        var results = await Task.WhenAll(store.DeleteAsync(1, 2), store.DeleteAsync(2, 1));

        Assert.Single(results, result => result.Status == UserAdministrationStatus.Success);
        Assert.Single(results, result => result.Status == UserAdministrationStatus.NotAuthorized);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users WHERE LOWER(TRIM(AuthLevel))='admin'"));
    }

    [MySqlFact]
    public async Task InternAccount_AuditFailureRollsBackCreationWithoutCreatingLoginUser()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'); CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure'");
        var store = new InternAccountStore(Config(db), Guard, NullLogger<InternAccountStore>.Instance);

        await Assert.ThrowsAsync<AuditPersistenceException>(() => store.SaveAsync(1, null, "Test Intern", "000intern"));

        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Accounts"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AuditEvents"));
    }

    [MySqlFact]
    public async Task UserAdministration_ConcurrentDuplicateEmailCreatesOnlyOneUser()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin')");
        var store = new UserAdministrationStore(Config(db), Guard);
        var command = new CreateUserCommand(1, "New User", "new@scene8.net", "User", [], false);

        var results = await Task.WhenAll(store.CreateAsync(command), store.CreateAsync(command));

        Assert.Single(results, result => result.Status == UserAdministrationStatus.Success);
        Assert.Single(results, result => result.Status == UserAdministrationStatus.DuplicateEmail);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users WHERE Email='new@scene8.net'"));
    }

    [MySqlFact]
    public async Task UserAdministration_FailedDeletionPreservesUserAndAccess()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'),(2,'user@scene8.net','User','User'); INSERT INTO Accounts (AccountId,Fund,AccountNumber) VALUES (1,'Fund A','1'); INSERT INTO AccountsUsers VALUES (2,1); UPDATE Users SET DefaultAccount=1 WHERE Id=2; CREATE TRIGGER FailUserDelete BEFORE DELETE ON Users FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure'");
        var store = new UserAdministrationStore(Config(db), Guard);

        await Assert.ThrowsAsync<MySqlException>(() => store.DeleteAsync(1, 2));

        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users WHERE Id=2 AND DefaultAccount=1"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers WHERE UserId=2 AND AccountId=1"));
    }

    [MySqlFact]
    public async Task UserAdministration_CombinedUpdateFailureRollsBackProfileAndLinks()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'),(2,'user@scene8.net','Original','User'); INSERT INTO Accounts (AccountId,Fund,AccountNumber) VALUES (1,'Fund A','1'),(2,'Fund B','2'); INSERT INTO AccountsUsers VALUES (2,1); UPDATE Users SET DefaultAccount=1 WHERE Id=2; CREATE TRIGGER FailUnlink BEFORE DELETE ON AccountsUsers FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure'");
        var store = new UserAdministrationStore(Config(db), Guard);

        await Assert.ThrowsAsync<MySqlException>(() => store.UpdateAsync(new(1, 2, "Changed", null, "Viewer", 2, 1, false)));

        Assert.Equal("Original", await ScalarAsync(db.RuntimeConnection, "SELECT Name FROM Users WHERE Id=2"));
        Assert.Equal("User", await ScalarAsync(db.RuntimeConnection, "SELECT AuthLevel FROM Users WHERE Id=2"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT DefaultAccount FROM Users WHERE Id=2"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers WHERE UserId=2 AND AccountId=1"));
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers WHERE UserId=2 AND AccountId=2"));
    }

    [MySqlFact]
    public async Task UserAdministration_ChangesLinksAndDefaultAtomicallyAndPreservesAccountsOnDelete()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'),(2,'user@scene8.net','User','User'); INSERT INTO Accounts (AccountId,Fund,AccountNumber) VALUES (1,'Fund A','1'),(2,'Fund B','2'); INSERT INTO AccountsUsers VALUES (2,1); UPDATE Users SET DefaultAccount=1 WHERE Id=2");
        var store = new UserAdministrationStore(Config(db), Guard);

        Assert.Equal(UserAdministrationStatus.Success, (await store.UpdateAsync(new(1, 2, "Changed", null, null, 2, 1, false))).Status);
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT DefaultAccount FROM Users WHERE Id=2"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers WHERE UserId=2 AND AccountId=2"));
        Assert.Equal(UserAdministrationStatus.Success, (await store.DeleteAsync(1, 2)).Status);
        Assert.Equal("0", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM AccountsUsers WHERE UserId=2"));
        Assert.Equal("2", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Accounts"));
    }

    [MySqlFact]
    public async Task InternAccount_TypoPreservesIdentityAndRecreationRetainsDonations()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin')");
        var store = new InternAccountStore(Config(db), Guard, NullLogger<InternAccountStore>.Instance);
        var users = new UserAdministrationStore(Config(db), Guard);
        Assert.Equal(UserAdministrationStatus.InvalidAccount, (await users.CreateAsync(new(1, "Test Intern", "intern@scene8.net", "Intern", [], false))).Status);
        var account = await store.SaveAsync(1, null, "Test Intrn", "000intern");
        await Donations(db).ReplaceCanonicalDonationsAsync([
            SourceDonation("intern-donor", "Synthetic Donor", internSourceId: "000intern", internDisplayName: "Test Intern")], default);
        var corrected = await store.SaveAsync(1, account.AccountId, "Test Intern", "000intern");
        Assert.Equal(account.AccountId, corrected.AccountId);
        Assert.Equal(account.Fund, corrected.Fund);
        Assert.Equal("000intern", corrected.InternSourceId);
        Assert.Equal("Test Intern", corrected.DisplayName);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Accounts WHERE AccountKind='Intern' AND AccountingClass IS NULL AND AccountNumber IS NULL"));
        await store.DeleteAsync(1, account.AccountId);
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM DonationData WHERE InternSourceId='000intern' AND SourceEvidenceJson IS NOT NULL"));
        var recreated = await store.SaveAsync(1, null, "Test Intern", "000intern");
        Assert.NotEqual(account.AccountId, recreated.AccountId);
        var reader = new DonationReadRepository(Config(db), Guard, NullLogger<DonationReadRepository>.Instance);
        Assert.Single(await reader.GetInternDonationsByInternNameAndDateRangeAsync("000intern", Date.AddDays(-1), Date.AddDays(1)));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("1", await ScalarAsync(db.RuntimeConnection, "SELECT COUNT(*) FROM Accounts"));
    }

    [MySqlFact]
    public async Task UserRepository_LanguageAndIdentityBindingDoNotOverwriteAdministration()
    {
        var db = await mysql.CreateDatabaseAsync();
        await ExecuteAsync(db.SetupConnection, "INSERT INTO Users (Id,Email,Name,AuthLevel) VALUES (1,'admin@scene8.net','Admin','Admin'),(2,'user@scene8.net','Original','Admin')");
        var users = new UserRepository(Config(db), Guard, NullLogger<UserRepository>.Instance);
        var store = new UserAdministrationStore(Config(db), Guard);
        var staleUser = await users.GetByIdAsync(2);
        Assert.Equal(UserAdministrationStatus.Success, (await store.UpdateAsync(new(1, 2, "Changed", null, "User", null, null, false))).Status);

        Assert.True(await users.UpdateLanguageAsync(staleUser!.Id, "es-US"));
        var bound = await users.TryBindGoogleIdAsync(staleUser.Id, staleUser.Email, "google-test");
        Assert.NotNull(bound);
        Assert.Equal("User", bound!.AuthLevel);
        Assert.Equal("Changed", bound.Name);
        Assert.Equal("es-US", bound.Language);
        Assert.Null(await users.TryBindGoogleIdAsync(staleUser.Id, staleUser.Email, "other-google"));
    }

    private static IConfiguration Config(TestDatabase db) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:default"] = db.RuntimeConnection }).Build();
    private static DonationRepository Donations(TestDatabase db) => new(Config(db), Guard, NullLogger<DonationRepository>.Instance);
    private RollbackExecutor Rollback(TestDatabase db) => new(Config(db), new TestOutputLogger<RollbackExecutor>(output),
        new RollbackRepository(Config(db), NullLogger<RollbackRepository>.Instance, Guard));
    private static CanonicalDonationWriteModel Donation(string identity = "testdonor", decimal amount = 10,
        IReadOnlyList<DonorContactWriteModel>? contacts = null, DateTime? date = null) =>
        SourceDonation(identity, identity, date: date, contacts: contacts, amount: amount);

    private static CanonicalDonationWriteModel SourceDonation(string sourceId, string name, bool soft = false,
        DateTime? date = null, string fund = "Test Fund", IReadOnlyList<DonorContactWriteModel>? contacts = null,
        decimal amount = 10, string? internSourceId = null, string? internDisplayName = null)
    {
        var emails = contacts is { Count: > 0 } ? contacts.Select(contact => (string?)contact.ContactValue) : [null];
        var validation = new DonationUploadNormalizer().Normalize(emails.Select((email, index) => new DonationSourceRow
        {
            RowNumber = index + 2, Date = date ?? Date, Amount = amount, Fund = fund, GiftImportId = "fixture",
            ConstituentId = soft ? "employer" : sourceId, SoftCreditRecipientId = soft ? sourceId : null,
            AccountName = soft ? "Employer" : name, PrimaryAddressee = soft ? "Employer" : name,
            SoftCreditName = soft ? name : null, PaymentMethod = "Check", GiftType = "Donation", Email = email,
            InternSourceId = internSourceId, InternDisplayName = internDisplayName
        }));
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Message)));
        var row = Assert.Single(validation.Donations);
        return new CanonicalDonationWriteModel
        {
            Donor = new DonorWriteModel
            {
                Fund = row.Fund, DisplayName = row.DonorDisplayName, IdentityKey = "id:" + row.CreditedDonorSourceId,
                ConstituentId = row.CreditedDonorSourceId, ResolutionSource = "SourceId", ResolutionVersion = 2
            },
            Date = row.Date, Amount = row.Amount, Fund = row.Fund, AccountName = row.AccountName,
            GiftImportId = row.GiftImportId, ConstituentId = row.ConstituentId,
            SoftCreditRecipientId = row.SoftCreditRecipientId, IsSoftCredit = row.IsSoftCredit || row.IsThirdPartySoftCredit,
            PaymentMethod = row.PaymentMethod, GiftType = row.GiftType, PrimaryAddressee = row.PrimaryAddressee,
            SoftCreditName = row.SoftCreditName, InternName = row.InternDisplayName, InternSourceId = row.InternSourceId,
            SourceEvidenceJson = JsonSerializer.Serialize(row)
        };
    }
    private static DonorContactWriteModel Email(string value) => new() { ContactType = "Email", ContactValue = value, NormalizedValue = value };
    private static AccountingImportRowDto Accounting(DateTime date, double amount) => new()
    {
        AccountingClass = "Test Class", Date = date, Num = "TEST", Amount = amount,
        AccountNumber = "1000", Account = "Test Account", Type = "Deposit"
    };
    private sealed class AvailableDatabase : IDatabaseGuard
    {
        public bool IsAvailable => true;
        public void ThrowIfUnavailable() { }
    }
    private sealed class NoProgress : IImportProgressService
    {
        public void Start(string id, string importType, string ownerUserId) { }
        public void AddStep(string id, string stepName, string status = "Starting...") { }
        public void UpdateStep(string id, string stepName, string status, string? details = null) { }
        public void CompleteStep(string id, string stepName, string completionStatus, string? details = null) { }
        public void Report(string id, int totalRows, int insertedRows, int failedRows, string? status = null) { }
        public void SetExpected(string id, int expectedRows) { }
        public void SetStatus(string id, string status) { }
        public void AddErrors(string id, IEnumerable<string> errors) { }
        public void Complete(string id) { }
    }
}
