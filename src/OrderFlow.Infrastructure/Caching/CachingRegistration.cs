using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.Caching;

public static class CachingRegistration
{
    public static IServiceCollection AddOrderFlowCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
            var config = ConfigurationOptions.Parse(options.Configuration);
            config.ConnectTimeout = options.ConnectTimeoutMs;
            config.AbortOnConnectFail = true;
            config.ConnectRetry = 2;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<IProductCache, RedisProductCache>();
        return services;
    }
}
