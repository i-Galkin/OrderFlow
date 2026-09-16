using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace OrderFlow.Tests.Support;

/// <summary>
/// Integration tests need real Postgres and Redis instances (docker compose provides both).
/// When they are not reachable the tests are skipped instead of failing the local build.
/// </summary>
public static class TestInfrastructure
{
    public static string PostgresConnectionString =>
        Environment.GetEnvironmentVariable("ORDERFLOW_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Database=orderflow_test;Username=postgres;Password=postgres;Timeout=3";

    public static string RedisConfiguration =>
        Environment.GetEnvironmentVariable("ORDERFLOW_TEST_REDIS") ?? "localhost:6379";

    private static readonly Lazy<bool> PostgresProbe = new(() =>
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString)
            {
                Database = "postgres",
                Timeout = 3
            };

            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    });

    private static readonly Lazy<bool> RedisProbe = new(() =>
    {
        try
        {
            var options = ConfigurationOptions.Parse(RedisConfiguration);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;

            using var multiplexer = ConnectionMultiplexer.Connect(options);
            return multiplexer.IsConnected;
        }
        catch
        {
            return false;
        }
    });

    public static bool PostgresAvailable => PostgresProbe.Value;

    public static bool RedisAvailable => RedisProbe.Value;
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!TestInfrastructure.PostgresAvailable)
        {
            Skip = "Postgres is not reachable; start docker compose to run this test.";
        }
    }
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!TestInfrastructure.PostgresAvailable)
        {
            Skip = "Postgres is not reachable; start docker compose to run this test.";
        }
    }
}

public sealed class PostgresRedisFactAttribute : FactAttribute
{
    public PostgresRedisFactAttribute()
    {
        if (!TestInfrastructure.PostgresAvailable || !TestInfrastructure.RedisAvailable)
        {
            Skip = "Postgres and Redis are required; start docker compose to run this test.";
        }
    }
}
