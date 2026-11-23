using cya2.Extensions;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class MySqlConnectionStringConfigurationTests
{
    [Theory]
    [InlineData("1", 1u)]
    [InlineData("15", 15u)]
    [InlineData("45", 45u)]
    [InlineData("60", 60u)]
    public void GetMaximumPoolSize_UsesConfiguredValue(string value, uint expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:MaximumPoolSize"] = value
        }).Build();

        var maximumPoolSize = MySqlConnectionStringConfiguration.GetMaximumPoolSize(configuration);
        var valid = MySqlConnectionStringConfiguration.TryNormalize(
            "Server=localhost;Database=cya2_test;User ID=test;Password=test;MaximumPoolSize=15;",
            maximumPoolSize,
            out var normalizedConnectionString);

        Assert.True(valid);
        Assert.Equal(expected, new MySqlConnectionStringBuilder(normalizedConnectionString).MaximumPoolSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("4294967296")]
    public void GetMaximumPoolSize_DefaultsTo45ForMissingOrInvalidValues(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:MaximumPoolSize"] = value
        }).Build();

        Assert.Equal(45u, MySqlConnectionStringConfiguration.GetMaximumPoolSize(configuration));
    }

    [Fact]
    public void TryNormalize_UsesConfiguredPoolSizeForSupportedConnectionString()
    {
        var result = MySqlConnectionStringConfiguration.TryNormalize(
            "Server=localhost;Database=cya2_test;User ID=test;Password=test;SslMode=Disabled;",
            15u,
            out var normalizedConnectionString);

        Assert.True(result);
        Assert.Equal(15u, new MySqlConnectionStringBuilder(normalizedConnectionString).MaximumPoolSize);
    }

    [Fact]
    public void TryNormalize_ReturnsFalseForUnsupportedSslMode()
    {
        var result = MySqlConnectionStringConfiguration.TryNormalize(
            "Server=localhost;Database=cya2_test;User ID=test;Password=test;SslMode=None;",
            15u,
            out var normalizedConnectionString);

        Assert.False(result);
        Assert.Empty(normalizedConnectionString);
    }
}
