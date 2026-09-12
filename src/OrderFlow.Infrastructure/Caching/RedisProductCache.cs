using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Dtos;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.Caching;

/// <summary>
/// Cache-aside store for the product read model. Product documents are small and read far
/// more often than they change, so a flat key per product is enough.
/// </summary>
public sealed class RedisProductCache : IProductCache
{
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
                return null;
            }

            return JsonSerializer.Deserialize<ProductDto>(value.ToString(), SerializerOptions);
        }
        catch (RedisConnectionException ex)
        {
            // A cache outage must not take the read path down with it.
            _logger.LogWarning(ex, "Redis unavailable while reading product {ProductId}", productId);
            return null;
        }
    }

    public async Task SetAsync(ProductDto product, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(product, SerializerOptions);
        await _redis.GetDatabase().StringSetAsync(
            CacheKey(product.Id),
            payload,
            TimeSpan.FromSeconds(_options.ProductTtlSeconds));
    }

    public async Task RemoveAsync(Guid productId, CancellationToken ct = default)
    {
        await _redis.GetDatabase().KeyDeleteAsync(CacheKey(productId));
    }
}
