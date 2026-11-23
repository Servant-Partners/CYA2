using Cya2.Core.DTOs;
using Cya2.Infrastructure.Repositories;
using MySql.Data.MySqlClient;
using Xunit;

namespace Cya2.Database.Tests;

public sealed class ContactStageBatchTests
{
    [Fact]
    public void RowLimit_PreservesAllRowsIncludingFinalPartialBatch()
    {
        var batch = new ContactStageBatch();
        var batchCounts = new List<int>();
        for (var index = 0; index < 10437; index++)
        {
            var contact = Contact("value");
            if (!batch.TryAdd($"id:{index}", contact))
            {
                batchCounts.Add(batch.Count);
                batch = new ContactStageBatch();
                Assert.True(batch.TryAdd($"id:{index}", contact));
            }
        }
        batchCounts.Add(batch.Count);
        Assert.Equal(42, batchCounts.Count);
        Assert.Equal(10437, batchCounts.Sum());
        Assert.Equal(187, batchCounts[^1]);
        Assert.All(batchCounts, count => Assert.InRange(count, 1, ContactStageBatch.MaximumRows));
    }

    [Fact]
    public void PayloadLimit_FlushesBeforeRowLimitForLargeUnicodeContacts()
    {
        var batch = new ContactStageBatch();
        var contact = Contact(new string('界', 500));
        while (batch.TryAdd(new string('界', 512), contact)) { }
        Assert.InRange(batch.Count, 1, ContactStageBatch.MaximumRows - 1);
        Assert.True(batch.EstimatedPayloadBytes <= ContactStageBatch.MaximumPayloadBytes);
    }

    [Fact]
    public void Command_ParameterizesValuesAndPreservesCaseAndLeadingZeros()
    {
        var batch = new ContactStageBatch();
        const string value = "O'Brien@example.invalid";
        Assert.True(batch.TryAdd("000AbC", Contact(value)));
        Assert.True(batch.TryAdd("000abc", Contact("second@example.invalid")));
        using var connection = new MySqlConnection();
        using var command = batch.CreateCommand(connection, null, 123);
        Assert.Equal(123, command.CommandTimeout);
        Assert.Equal(8, command.Parameters.Count);
        Assert.Equal("000AbC", command.Parameters["@Key0"].Value);
        Assert.Equal("000abc", command.Parameters["@Key1"].Value);
        Assert.Equal(value, command.Parameters["@Value0"].Value);
        Assert.DoesNotContain(value, command.CommandText);
        Assert.Contains("@Normalized1", command.CommandText);
    }

    [Fact]
    public void EmptyBatch_CannotCreateInsert()
    {
        using var connection = new MySqlConnection();
        Assert.Throws<InvalidOperationException>(() => new ContactStageBatch().CreateCommand(connection, null, 30));
    }

    [Fact]
    public void OversizedRow_RejectsWithoutExposingContactValue()
    {
        var batch = new ContactStageBatch();
        var error = Assert.Throws<InvalidOperationException>(() => batch.TryAdd("key", Contact(new string('x', 300000))));
        Assert.DoesNotContain(new string('x', 100), error.Message);
        Assert.Equal(0, batch.Count);
    }

    private static DonorContactWriteModel Contact(string value) => new()
    {
        ContactType = "Email", ContactValue = value, NormalizedValue = value.ToUpperInvariant()
    };
}
