using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace OrderFlow.Infrastructure.Observability;

public static class ObservabilityRegistration
{
    /// <summary>
    /// Registers OpenTelemetry metrics shared by both hosts: the OrderFlow meter, .NET runtime,
    /// Npgsql and EF Core. Each host adds its own framework meters and exporter through
    /// <paramref name="configureMetrics"/>.
    /// </summary>
    public static IServiceCollection AddOrderFlowObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        string environmentName,
        Action<MeterProviderBuilder> configureMetrics)
    {
        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));

        if (!ObservabilityOptions.From(configuration).Enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceVersion: typeof(ObservabilityRegistration).Assembly.GetName().Version?.ToString(),
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environmentName)]))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(OrderFlowMetrics.MeterName)
                    .AddMeter("System.Runtime")
                    .AddMeter("Npgsql")
                    .AddMeter("Microsoft.EntityFrameworkCore");

                configureMetrics(metrics);
            });

        return services;
    }
}
