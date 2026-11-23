using Cya2.Core.Entities;
using Cya2.Core.Interfaces;
using Cya2.Application.Interfaces;
using Cya2.Infrastructure.Services;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace Cya2.Infrastructure.Repositories;

public sealed class InternAccountStore(IConfiguration configuration, IDatabaseGuard guard, ILogger<InternAccountStore> logger) : IInternAccountStore
{
    public async Task<Account> SaveAsync(int actorId, int? accountId, string name, string sourceId)
    {
        name = name.Trim(); sourceId = sourceId.Trim();
        if (name.Length is < 1 or > 255 || sourceId.Length is < 1 or > 255)
            throw new ArgumentException("Intern name and ID are required and must be at most 255 characters.");
        guard.ThrowIfUnavailable();
        await using var connection = new MySqlConnection(configuration.GetConnectionString("default"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireAdmin(connection, transaction, actorId);
        var fund = "Intern: " + Guid.NewGuid().ToString("N");
        int id;
        if (accountId is { } existing)
        {
            var count = await connection.ExecuteAsync(@"UPDATE Accounts SET DisplayName=@name, InternSourceId=@sourceId
WHERE AccountId=@existing AND AccountKind='Intern'", new { name, sourceId, fund, existing }, transaction);
            if (count != 1) throw new InvalidOperationException("Intern account not found.");
            id = existing;
            fund = await connection.ExecuteScalarAsync<string>("SELECT Fund FROM Accounts WHERE AccountId=@id", new { id }, transaction) ?? string.Empty;
        }
        else
        {
            id = await connection.ExecuteScalarAsync<int>(@"INSERT INTO Accounts (Fund, AccountKind, InternSourceId, DisplayName, AccountingClass, AccountNumber)
VALUES (@fund, 'Intern', @sourceId, @name, NULL, NULL); SELECT LAST_INSERT_ID();", new { fund, sourceId, name }, transaction);
        }
        using var operation = AuditOperationScope.Begin(actorId);
        await TransactionalAudit.RecordAsync(connection, transaction, "InternAccountSaved", logger);
        await transaction.CommitAsync();
        return new Account { AccountId = id, Fund = fund, AccountKind = "Intern", InternSourceId = sourceId, DisplayName = name };
    }

    public async Task DeleteAsync(int actorId, int accountId)
    {
        guard.ThrowIfUnavailable();
        await using var connection = new MySqlConnection(configuration.GetConnectionString("default"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireAdmin(connection, transaction, actorId);
        var count = await connection.ExecuteAsync("DELETE FROM Accounts WHERE AccountId=@accountId AND AccountKind='Intern'", new { accountId }, transaction);
        if (count != 1) throw new InvalidOperationException("Intern account not found.");
        using var operation = AuditOperationScope.Begin(actorId);
        await TransactionalAudit.RecordAsync(connection, transaction, "InternAccountDeleted", logger);
        await transaction.CommitAsync();
    }

    private static async Task RequireAdmin(MySqlConnection connection, MySqlTransaction transaction, int actorId)
    {
        var role = await connection.ExecuteScalarAsync<string>("SELECT AuthLevel FROM Users WHERE Id=@actorId FOR UPDATE", new { actorId }, transaction);
        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
    }
}
