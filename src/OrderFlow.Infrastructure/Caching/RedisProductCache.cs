using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Infrastructure.Observability;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.Caching;

/// <summary>
/// Cache-aside store for the product read model. Product documents are small and read far
/// more often than they change, so a flat key per product is enough.
/// </summary>
public sealed class RedisProductCache : IProductCache
{
    private const string MetricsCacheName = "product";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisProductCache> _logger;

    public RedisProductCache(
        IConnectionMultiplexer redis,
        IOptions<RedisOptions> options,
        ILogger<RedisProductCache> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    public static string CacheKey(Guid productId) => $"product:{productId:N}";

    public async Task<ProductDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(CacheKey(productId));
            if (value.IsNullOrEmpty)
            {
                OrderFlowMetrics.RecordCacheRequest(MetricsCacheName, OrderFlowMetrics.CacheResults.Miss);
                return null;
            }

            OrderFlowMetrics.RecordCacheRequest(MetricsCacheName, OrderFlowMetrics.CacheResults.Hit);
            return JsonSerializer.Deserialize<ProductDto>(value.ToString(), SerializerOptions);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // A cache outage or timeout must not take the read path down with it.
            OrderFlowMetrics.RecordCacheRequest(MetricsCacheName, OrderFlowMetrics.CacheResults.Unavailable);
            _logger.LogWarning(ex, "Redis unavailable while reading product {ProductId}", productId);
            return null;
        }
    }

    public async Task SetAsync(ProductDto product, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(product, SerializerOptions);
        try
        {
            await _redis.GetDatabase().StringSetAsync(
                CacheKey(product.Id),
                payload,
                TimeSpan.FromSeconds(_options.ProductTtlSeconds));
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a cache failure; let it propagate uncounted, as it did before
            // this catch existed.
            throw;
        }
        catch
        {
            // Count only; the write path stays unguarded.
            OrderFlowMetrics.RecordCacheWriteError(MetricsCacheName);
            throw;
        }
    }

    public async Task RemoveAsync(Guid productId, CancellationToken ct = default)
    {
        await _redis.GetDatabase().KeyDeleteAsync(CacheKey(productId));
    }
}
