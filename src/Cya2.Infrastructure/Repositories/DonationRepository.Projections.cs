using System.Diagnostics;
using System.Text.Json;
using Cya2.Core.DTOs;
using Cya2.Core.Imports;
using Cya2.Core.Services;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace Cya2.Infrastructure.Repositories;

public sealed partial class DonationRepository
{
    private static async Task CaptureAffectedGroupsAsync(MySqlConnection connection, MySqlTransaction transaction, DateTime boundary, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"DROP TEMPORARY TABLE IF EXISTS TempAffectedGroups;
CREATE TEMPORARY TABLE TempAffectedGroups
(
    StageId BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    IdentityKey VARCHAR(512) COLLATE utf8mb4_bin NOT NULL,
    Fund VARCHAR(255) COLLATE utf8mb4_bin NOT NULL,
    ChangedFrom DATETIME NOT NULL
) ENGINE=InnoDB;
INSERT INTO TempAffectedGroups (IdentityKey, Fund, ChangedFrom)
SELECT donor.IdentityKey, UPPER(TRIM(donation.Fund)) COLLATE utf8mb4_bin, MIN(donation.Date) FROM DonationData donation
JOIN Donors donor ON donor.Id = donation.DonorId WHERE donation.Date >= @Boundary
GROUP BY donor.IdentityKey, UPPER(TRIM(donation.Fund)) COLLATE utf8mb4_bin;
INSERT INTO TempAffectedGroups (IdentityKey, Fund, ChangedFrom)
SELECT IdentityKey, UPPER(TRIM(Fund)) COLLATE utf8mb4_bin, MIN(Date) FROM TempImportDonations GROUP BY IdentityKey, UPPER(TRIM(Fund)) COLLATE utf8mb4_bin;";
        command.Parameters.AddWithValue("@Boundary", boundary);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task RebuildAffectedProjectionsAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken ct, Action<DonationPersistenceProgress> reportProgress)
    {
        var totalTimer = Stopwatch.StartNew();
        var evidenceTimer = Stopwatch.StartNew();
        var evidenceRows = 0;
        var evidence = new Dictionary<string, List<NormalizedDonation>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT donor.IdentityKey, donation.SourceEvidenceJson
FROM DonationData donation JOIN Donors donor ON donor.Id = donation.DonorId
JOIN (SELECT DISTINCT IdentityKey FROM TempAffectedGroups) affected ON affected.IdentityKey = donor.IdentityKey
WHERE donation.SourceEvidenceJson IS NOT NULL AND donation.IsAnonymous = 0 AND donation.IsSoftCredit = 0";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                var row = JsonSerializer.Deserialize<NormalizedDonation>(reader.GetString(1))
                    ?? throw new InvalidOperationException("Donation source evidence is invalid.");
                if (!evidence.TryGetValue(key, out var rows)) evidence[key] = rows = [];
                rows.Add(row);
                evidenceRows++;
            }
        }
        evidenceTimer.Stop();
        var preparationTimer = Stopwatch.StartNew();
        await ExecuteImportCommandAsync(connection, transaction, @"DELETE FROM TempImportContacts;
UPDATE TempImportDonors SET HasDirect = 1;
INSERT IGNORE INTO TempImportDonors (Fund, IdentityKey, DisplayName, ResolutionSource, ResolutionVersion, ConstituentId, HasDirect)
SELECT '', donor.IdentityKey, donor.DisplayName, donor.ResolutionSource, donor.ResolutionVersion, donor.ConstituentId, 1
FROM Donors donor JOIN (SELECT DISTINCT IdentityKey FROM TempAffectedGroups) affected ON affected.IdentityKey = donor.IdentityKey;", ct);
        preparationTimer.Stop();

        var projector = new DonationContactProjector();
        var projected = 0;
        var contactRows = 0;
        var commandCount = 0;
        var calculationTimer = new Stopwatch();
        var stagingTimer = new Stopwatch();
        var batch = new ContactStageBatch();
        reportProgress(new("ImportContactProjection", 0, evidence.Count));
        foreach (var (key, rows) in evidence)
        {
            ct.ThrowIfCancellationRequested();
            var id = rows[0].CreditedDonorSourceId;
            if (id is null) continue;
            calculationTimer.Start();
            var projection = projector.Project(id, rows, ct);
            if (projection.HasAmbiguousAddress)
                throw new InvalidOperationException("Conflicting same-day address observations require correction before publication.");
            var contacts = projection.Contacts.Select(contact => new DonorContactWriteModel
            {
                ContactType = contact.Kind.ToString(), ContactValue = contact.Value, NormalizedValue = contact.NormalizedValue
            }).ToList();
            if (projection.Address is { } address)
            {
                Add("AddressLine1", address.Line1); Add("City", address.City); Add("State", address.State);
                Add("PostalCode", address.PostalCode); Add("Country", address.Country);
            }
            calculationTimer.Stop();
            foreach (var contact in contacts)
            {
                ct.ThrowIfCancellationRequested();
                if (!batch.TryAdd(key, contact))
                {
                    await FlushBatchAsync();
                    if (!batch.TryAdd(key, contact))
                        throw new InvalidOperationException("The projected contact could not be staged.");
                }
                contactRows++;
            }
            projected++;
            if (projected % 25 == 0 || projected == evidence.Count)
                reportProgress(new("ImportContactProjection", projected, evidence.Count));
            void Add(string type, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) contacts.Add(new DonorContactWriteModel
                {
                    ContactType = type, ContactValue = value,
                    NormalizedValue = DonorIdentityResolver.NormalizeWhitespace(value).ToUpperInvariant()
                });
            }
        }
        await FlushBatchAsync();
        _logger.LogInformation("Contact projection diagnostics: DonorCount={DonorCount}, ProjectedDonorCount={ProjectedDonorCount}, EvidenceRowCount={EvidenceRowCount}, ContactRowCount={ContactRowCount}, StagingCommandCount={StagingCommandCount}, EvidenceElapsedMs={EvidenceElapsedMs}, PreparationElapsedMs={PreparationElapsedMs}, CalculationElapsedMs={CalculationElapsedMs}, StagingElapsedMs={StagingElapsedMs}, TotalElapsedMs={TotalElapsedMs}",
            evidence.Count, projected, evidenceRows, contactRows, commandCount, evidenceTimer.ElapsedMilliseconds,
            preparationTimer.ElapsedMilliseconds, calculationTimer.ElapsedMilliseconds, stagingTimer.ElapsedMilliseconds, totalTimer.ElapsedMilliseconds);

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0) return;
            ct.ThrowIfCancellationRequested();
            stagingTimer.Start();
            try
            {
                await using var command = batch.CreateCommand(connection, transaction, ImportCommandTimeoutSeconds);
                var inserted = await command.ExecuteNonQueryAsync(ct);
                if (inserted != batch.Count)
                    throw new InvalidOperationException("Staged contact count did not match the batch count.");
                commandCount++;
                batch = new ContactStageBatch();
            }
            finally
            {
                stagingTimer.Stop();
            }
        }
    }

    private static async Task ReclassifyAffectedGroupsAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken ct, Action<DonationPersistenceProgress> reportProgress)
    {
        await ExecuteImportCommandAsync(connection, transaction, @"DROP TEMPORARY TABLE IF EXISTS TempFrequencyGroups;
CREATE TEMPORARY TABLE TempFrequencyGroups
(
    StageId BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    DonorId BIGINT NOT NULL,
    Fund VARCHAR(255) COLLATE utf8mb4_bin NOT NULL,
    ChangedFrom DATETIME NOT NULL
) ENGINE=InnoDB;
INSERT INTO TempFrequencyGroups (DonorId, Fund, ChangedFrom)
SELECT donor.Id, affected.Fund, MIN(affected.ChangedFrom)
FROM TempAffectedGroups affected JOIN Donors donor ON donor.IdentityKey = affected.IdentityKey
GROUP BY donor.Id, affected.Fund;
DROP TEMPORARY TABLE IF EXISTS TempImportFrequency;
CREATE TEMPORARY TABLE TempImportFrequency (Id BIGINT PRIMARY KEY, Frequency TINYINT NOT NULL)", ct);

        const int pageSize = 500;
        var changes = new List<(long Id, int Frequency)>();
        var classified = 0;
        long afterGroup = 0;
        while (true)
        {
            var groups = new List<(long StageId, long DonorId, string Fund, DateTime ChangedFrom)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = ImportCommandTimeoutSeconds;
                command.CommandText = "SELECT StageId, DonorId, Fund, ChangedFrom FROM TempFrequencyGroups WHERE StageId > @After ORDER BY StageId LIMIT 64";
                command.Parameters.AddWithValue("@After", afterGroup);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) groups.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetDateTime(3)));
            }
            if (groups.Count == 0) break;
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                var anchor = await FindFrequencyAnchorAsync(connection, transaction, group.DonorId, group.Fund, group.ChangedFrom, ct);
                var state = new DonorFrequencyService.FrequencySequence();
                FrequencyHistoryRow? pending = null;
                DateTime? afterDate = null;
                long afterId = 0;
                while (true)
                {
                    var page = new List<FrequencyHistoryRow>(pageSize);
                    await using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandTimeout = ImportCommandTimeoutSeconds;
                        command.CommandText = @"SELECT Id, Date, Amount, Frequency FROM DonationData
WHERE DonorId = @DonorId AND UPPER(TRIM(Fund)) COLLATE utf8mb4_bin = @Fund
AND (@Start IS NULL OR Date >= @Start)
AND (@After IS NULL OR Date > @After OR (Date = @After AND Id > @AfterId))
ORDER BY Date, Id LIMIT 500";
                        command.Parameters.AddWithValue("@DonorId", group.DonorId);
                        command.Parameters.AddWithValue("@Fund", group.Fund);
                        command.Parameters.AddWithValue("@Start", (object?)anchor?.Date.AddMonths(-12) ?? DBNull.Value);
                        command.Parameters.AddWithValue("@After", (object?)afterDate ?? DBNull.Value);
                        command.Parameters.AddWithValue("@AfterId", afterId);
                        await using var reader = await command.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct)) page.Add(new(reader.GetInt64(0), reader.GetDateTime(1), reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
                    }
                    if (page.Count == 0) break;
                    foreach (var row in page)
                    {
                        ct.ThrowIfCancellationRequested();
                        state.Add(new DonorGiftRecord { Date = row.Date, Amount = row.Amount });
                        if (pending is not null) await StageAsync(pending, (int)state.Previous);
                        pending = row;
                    }
                    afterDate = page[^1].Date;
                    afterId = page[^1].Id;
                }
                if (pending is not null) await StageAsync(pending, (int)state.Current);
                classified++;
                reportProgress(new("ImportFrequencyCalculation", classified));
                afterGroup = group.StageId;

                async Task StageAsync(FrequencyHistoryRow row, int frequency)
                {
                    // Context is read-only; only the proven checkpoint and its suffix can change.
                    if (anchor is not null && (row.Date < anchor.Value.Date || (row.Date == anchor.Value.Date && row.Id < anchor.Value.Id))) return;
                    if (row.Frequency == frequency) return;
                    changes.Add((row.Id, frequency));
                    if (changes.Count == pageSize) await FlushAsync();
                }
            }
        }
        await FlushAsync();
        reportProgress(new("ImportFrequencyWrites"));
        await ExecuteImportCommandAsync(connection, transaction, @"UPDATE DonationData donation JOIN TempImportFrequency staged ON staged.Id = donation.Id
SET donation.Frequency = staged.Frequency", ct);

        async Task FlushAsync()
        {
            if (changes.Count == 0) return;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = ImportCommandTimeoutSeconds;
            var values = new List<string>();
            for (var index = 0; index < changes.Count; index++)
            {
                values.Add($"(@Id{index}, @Frequency{index})");
                command.Parameters.AddWithValue($"@Id{index}", changes[index].Id);
                command.Parameters.AddWithValue($"@Frequency{index}", changes[index].Frequency);
            }
            command.CommandText = "INSERT INTO TempImportFrequency VALUES " + string.Join(',', values);
            if (await command.ExecuteNonQueryAsync(ct) != changes.Count)
                throw new InvalidOperationException("Frequency staging count did not match the calculated updates.");
            changes.Clear();
        }
    }

    private sealed record FrequencyHistoryRow(long Id, DateTime Date, decimal Amount, int? Frequency);

    private static async Task<(long Id, DateTime Date)?> FindFrequencyAnchorAsync(
        MySqlConnection connection, MySqlTransaction transaction, long donorId, string fund, DateTime changedFrom, CancellationToken ct)
    {
        // A consecutive-month pair proves Monthly without trusting persisted frequency or earlier pattern state.
        // Replay includes the checkpoint's full twelve-month amount context before emitting any updates.
        foreach (var months in new[] { 4, 13, 26 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = ImportCommandTimeoutSeconds;
            command.CommandText = @"SELECT Id, Date FROM
(
    SELECT Id, Date, LAG(Date) OVER (ORDER BY Date, Id) AS PreviousDate
    FROM DonationData WHERE DonorId = @DonorId AND UPPER(TRIM(Fund)) COLLATE utf8mb4_bin = @Fund
    AND Date >= @Start AND Date < @ChangedFrom
) history
WHERE (YEAR(Date) * 12 + MONTH(Date)) - (YEAR(PreviousDate) * 12 + MONTH(PreviousDate)) = 1
ORDER BY Date DESC, Id DESC LIMIT 1";
            command.Parameters.AddWithValue("@DonorId", donorId);
            command.Parameters.AddWithValue("@Fund", fund);
            command.Parameters.AddWithValue("@Start", changedFrom.AddMonths(-months));
            command.Parameters.AddWithValue("@ChangedFrom", changedFrom);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return (reader.GetInt64(0), reader.GetDateTime(1));
        }
        // No proven boundary: replay only this affected donor/fund from its beginning, still in bounded pages.
        return null;
    }
}
