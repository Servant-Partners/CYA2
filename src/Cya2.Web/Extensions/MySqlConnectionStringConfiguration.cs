using System.Globalization;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace cya2.Extensions;

public static class MySqlConnectionStringConfiguration
{
    private const uint DefaultMaximumPoolSize = 45u;

    public static uint GetMaximumPoolSize(IConfiguration configuration)
        => uint.TryParse(configuration["Database:MaximumPoolSize"], NumberStyles.None, CultureInfo.InvariantCulture, out var maximumPoolSize)
            && maximumPoolSize > 0
                ? maximumPoolSize
                : DefaultMaximumPoolSize;

    public static bool TryNormalize(string? connectionString, uint maximumPoolSize, out string normalizedConnectionString)
    {
        normalizedConnectionString = string.Empty;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return true;
        }

        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                MaximumPoolSize = maximumPoolSize
            };

            normalizedConnectionString = builder.ConnectionString;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
