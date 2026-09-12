using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.Health;

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly OrderFlowDbContext _db;

    public PostgresHealthCheck(OrderFlowDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("postgres reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("postgres unreachable", ex);
        }
    }
}

public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await _redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"redis ping {latency.TotalMilliseconds:F1}ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("redis unreachable", ex);
        }
    }
}

/// <summary>
/// Confirms the broker is reachable and that the orders topic exists.
/// </summary>
public sealed class KafkaHealthCheck : IHealthCheck
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaHealthCheck> _logger;

    public KafkaHealthCheck(IOptions<KafkaOptions> options, ILogger<KafkaHealthCheck> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = _options.BootstrapServers
            }).Build();

            var metadata = admin.GetMetadata(_options.Topic, TimeSpan.FromMilliseconds(500));

            return Task.FromResult(metadata.Topics.Count > 0
                ? HealthCheckResult.Healthy($"kafka metadata for {_options.Topic}")
                : HealthCheckResult.Unhealthy($"topic {_options.Topic} not found"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("kafka unreachable", ex));
        }
    }
}
