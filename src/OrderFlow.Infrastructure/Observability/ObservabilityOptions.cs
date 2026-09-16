using Microsoft.Extensions.Configuration;

namespace OrderFlow.Infrastructure.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Registers the OpenTelemetry meter provider and the Prometheus scrape endpoint.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Worker only: host the HttpListener serving <c>/metrics</c> binds to. Defaults to localhost so
    /// a plain <c>dotnet run</c> on Windows needs no URL ACL; containers bind every interface.
    /// </summary>
    public string MetricsHost { get; set; } = "localhost";

    /// <summary>Worker only: port of the <c>/metrics</c> listener.</summary>
    public int MetricsPort { get; set; } = 9464;

    /// <summary>Worker only: how often consumer lag is sampled. 0 disables the lag monitor.</summary>
    public int ConsumerLagPollSeconds { get; set; } = 15;

    public static ObservabilityOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
}
