using System.Text;
using Cya2.Core.DTOs;
using MySql.Data.MySqlClient;

namespace Cya2.Infrastructure.Repositories;

internal sealed class ContactStageBatch
{
    internal const int MaximumRows = 250;
    internal const int MaximumPayloadBytes = 256 * 1024;
    private const int CommandOverheadBytes = 512;
    private readonly List<(string Key, DonorContactWriteModel Contact)> _rows = [];

    internal int Count => _rows.Count;
    internal long EstimatedPayloadBytes { get; private set; } = CommandOverheadBytes;

    internal bool TryAdd(string key, DonorContactWriteModel contact)
    {
        var rowBytes = 256L + 2L * (Encoding.UTF8.GetByteCount(key)
            + Encoding.UTF8.GetByteCount(contact.ContactType)
            + Encoding.UTF8.GetByteCount(contact.ContactValue)
            + Encoding.UTF8.GetByteCount(contact.NormalizedValue));
        if (rowBytes + CommandOverheadBytes > MaximumPayloadBytes)
            throw new InvalidOperationException("A projected contact exceeds the staging batch payload limit.");
        if (Count >= MaximumRows || EstimatedPayloadBytes + rowBytes > MaximumPayloadBytes)
            return false;
        _rows.Add((key, contact));
        EstimatedPayloadBytes += rowBytes;
        return true;
    }

    internal MySqlCommand CreateCommand(MySqlConnection connection, MySqlTransaction? transaction, int commandTimeout)
    {
        if (Count == 0) throw new InvalidOperationException("Cannot stage an empty contact batch.");
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeout;
        var sql = new StringBuilder("INSERT INTO TempImportContacts (Fund, IdentityKey, ContactType, ContactValue, NormalizedValue) VALUES ");
        for (var index = 0; index < Count; index++)
        {
            if (index > 0) sql.Append(',');
            sql.Append($"('', @Key{index}, @Type{index}, @Value{index}, @Normalized{index})");
            var (key, contact) = _rows[index];
            command.Parameters.AddWithValue($"@Key{index}", key);
            command.Parameters.AddWithValue($"@Type{index}", contact.ContactType);
            command.Parameters.AddWithValue($"@Value{index}", contact.ContactValue);
            command.Parameters.AddWithValue($"@Normalized{index}", contact.NormalizedValue);
        }
        command.CommandText = sql.ToString();
        return command;
    }
}
