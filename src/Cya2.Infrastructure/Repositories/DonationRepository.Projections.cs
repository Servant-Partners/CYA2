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
    Fund VARCHAR(255) NOT NULL
) ENGINE=InnoDB;
INSERT INTO TempAffectedGroups (IdentityKey, Fund)
SELECT DISTINCT donor.IdentityKey, donation.Fund FROM DonationData donation
JOIN Donors donor ON donor.Id = donation.DonorId WHERE donation.Date >= @Boundary;
INSERT INTO TempAffectedGroups (IdentityKey, Fund) SELECT DISTINCT IdentityKey, Fund FROM TempImportDonations;";
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
        var rows = new List<(long Id, long DonorId, string Fund, DateTime Date, decimal Amount, int? Frequency)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT donation.Id, donation.DonorId, donation.Fund, donation.Date, donation.Amount, donation.Frequency
FROM DonationData donation JOIN Donors donor ON donor.Id = donation.DonorId
JOIN (SELECT DISTINCT IdentityKey, Fund FROM TempAffectedGroups) affected
ON affected.IdentityKey = donor.IdentityKey AND affected.Fund = donation.Fund ORDER BY donation.Date, donation.Id";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetDateTime(3), reader.GetDecimal(4), reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }
        var classifier = new DonorFrequencyService();
        var changes = new List<(long Id, int Frequency)>();
        var classified = 0;
        reportProgress(new("ImportFrequencyCalculation", 0, rows.Count));
        foreach (var group in rows.GroupBy(row => (row.DonorId, Fund: row.Fund.Trim().ToUpperInvariant())))
        {
            ct.ThrowIfCancellationRequested();
            var history = group.Select(row => new DonorGiftRecord { Date = row.Date, Amount = row.Amount }).ToList();
            foreach (var row in group)
            {
                var frequency = (int)classifier.ClassifyGift(new DonorGiftRecord { Date = row.Date, Amount = row.Amount }, history).Frequency;
                if (frequency != row.Frequency) changes.Add((row.Id, frequency));
                classified++;
                if (classified % 250 == 0 || classified == rows.Count)
                    reportProgress(new("ImportFrequencyCalculation", classified, rows.Count));
            }
        }
        await ExecuteImportCommandAsync(connection, transaction, "DROP TEMPORARY TABLE IF EXISTS TempImportFrequency; CREATE TEMPORARY TABLE TempImportFrequency (Id BIGINT PRIMARY KEY, Frequency TINYINT NOT NULL)", ct);
        reportProgress(new("ImportFrequencyWrites"));
        foreach (var chunk in changes.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var values = new List<string>();
            for (var index = 0; index < chunk.Length; index++)
            {
                values.Add($"(@Id{index}, @Frequency{index})");
                command.Parameters.AddWithValue($"@Id{index}", chunk[index].Id);
                command.Parameters.AddWithValue($"@Frequency{index}", chunk[index].Frequency);
            }
            command.CommandText = "INSERT INTO TempImportFrequency VALUES " + string.Join(',', values);
            await command.ExecuteNonQueryAsync(ct);
        }
        await ExecuteImportCommandAsync(connection, transaction, @"UPDATE DonationData donation JOIN TempImportFrequency staged ON staged.Id = donation.Id
SET donation.Frequency = staged.Frequency", ct);
    }
}
