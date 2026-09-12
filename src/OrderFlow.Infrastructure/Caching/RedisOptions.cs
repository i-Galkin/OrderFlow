namespace OrderFlow.Infrastructure.Caching;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string Configuration { get; set; } = "localhost:6379";

    /// <summary>Time to live for cached product documents.</summary>
    public int ProductTtlSeconds { get; set; } = 600;

    public int ConnectTimeoutMs { get; set; } = 1000;
}
