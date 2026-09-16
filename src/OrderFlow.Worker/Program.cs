using OpenTelemetry.Metrics;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Observability;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Infrastructure.Processing;
using OrderFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddOrderFlowPersistence(builder.Configuration);
builder.Services.AddOrderFlowMessaging(builder.Configuration);
builder.Services.AddOrderFlowCaching(builder.Configuration);
builder.Services.AddOrderFlowProcessing();

var observability = ObservabilityOptions.From(builder.Configuration);
builder.Services.AddOrderFlowObservability(
    builder.Configuration,
    "orderflow-worker",
    builder.Environment.EnvironmentName,
    metrics => metrics.AddPrometheusHttpListener(listener =>
    {
        // The exporter builds its prefix through Uri, which rejects the HttpListener wildcards
        // containers need, so a wildcard host replaces the prefix on the listener itself.
        var wildcard = observability.MetricsHost is "*" or "+";
        listener.Host = wildcard ? "localhost" : observability.MetricsHost;
        listener.Port = observability.MetricsPort;

        if (wildcard)
        {
            listener.ConfigureHttpListener = (_, httpListener) =>
            {
                httpListener.Prefixes.Clear();
                httpListener.Prefixes.Add($"http://{observability.MetricsHost}:{observability.MetricsPort}/");
            };
        }
    }));

// Registered before the consumer so it starts regardless of how long the consumer's startup takes.
builder.Services.AddHostedService<KafkaLagMonitor>();
builder.Services.AddHostedService<OrderEventConsumer>();

var host = builder.Build();
host.Run();
