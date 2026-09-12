using Microsoft.Extensions.DependencyInjection;

namespace OrderFlow.Infrastructure.Health;

public static class HealthRegistration
{
    public static IServiceCollection AddOrderFlowHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: new[] { "ready" })
            .AddCheck<RedisHealthCheck>("redis", tags: new[] { "ready", "live" })
            .AddCheck<KafkaHealthCheck>("kafka", tags: new[] { "ready" });

        return services;
    }
}
