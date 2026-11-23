using System.Text.Json;
using Cya2.Core.Imports;
using Cya2.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace Cya2.Infrastructure.Repositories;

public sealed class DonationAccountMappingReader(IConfiguration configuration, IDatabaseGuard databaseGuard) : IDonationAccountMappingReader
{
    private sealed class MappingCounts
    {
        public int UnresolvedNormalDonations { get; set; }
        public int UnresolvedInternDonations { get; set; }
    }

    public async Task<DonationAccountMappingCounts> GetCountsAsync(IReadOnlyList<NormalizedDonation> donations, CancellationToken cancellationToken = default)
    {
        databaseGuard.ThrowIfUnavailable();
        var groups = donations.GroupBy(row => (row.Fund, row.InternSourceId))
            .Select(group => new { group.Key.Fund, group.Key.InternSourceId, Count = group.Count() });
        await using var connection = new MySqlConnection(configuration.GetConnectionString("default") ?? string.Empty);
        await connection.OpenAsync(cancellationToken);
        var normal = 0;
        var intern = 0;
        const string sql = @"
SELECT
    COALESCE(SUM(CASE WHEN input.InternSourceId IS NULL AND mapping.AccountId IS NULL THEN input.RowCount ELSE 0 END), 0) AS UnresolvedNormalDonations,
    COALESCE(SUM(CASE WHEN input.InternSourceId IS NOT NULL AND account.AccountId IS NULL THEN input.RowCount ELSE 0 END), 0) AS UnresolvedInternDonations
FROM JSON_TABLE(@Payload, '$[*]' COLUMNS (
    Fund VARCHAR(255) PATH '$.Fund',
    InternSourceId VARCHAR(255) PATH '$.InternSourceId' NULL ON EMPTY,
    RowCount INT PATH '$.Count'
)) input
LEFT JOIN NormalDonationAccountMappings mapping ON input.InternSourceId IS NULL AND mapping.Fund = input.Fund
LEFT JOIN Accounts account ON input.InternSourceId IS NOT NULL AND account.AccountKind = 'Intern'
    AND CAST(account.InternSourceId AS BINARY) = CAST(input.InternSourceId AS BINARY)";
        foreach (var batch in groups.Chunk(250))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = await connection.QuerySingleAsync<MappingCounts>(new CommandDefinition(
                sql, new { Payload = JsonSerializer.Serialize(batch) }, cancellationToken: cancellationToken));
            normal += counts.UnresolvedNormalDonations;
            intern += counts.UnresolvedInternDonations;
        }
        return new DonationAccountMappingCounts(normal, intern);
    }
}
