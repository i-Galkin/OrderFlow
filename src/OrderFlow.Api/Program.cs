using System.Text.Json.Serialization;
using OrderFlow.Api.Middleware;
using Scalar.AspNetCore;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Health;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Observability;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Infrastructure.Seeding;
using OrderFlow.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.AddOrderFlowPersistence(builder.Configuration);
builder.Services.AddOrderFlowMessaging(builder.Configuration);
builder.Services.AddOrderFlowCaching(builder.Configuration);
builder.Services.AddOrderFlowServices();
builder.Services.AddOrderFlowHealthChecks();
builder.Services.AddOrderFlowObservability(
    builder.Configuration,
    "orderflow-api",
    builder.Environment.EnvironmentName,
    metrics => metrics
        .AddMeter("Microsoft.AspNetCore.Hosting")
        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
        .AddMeter("Microsoft.AspNetCore.Diagnostics")
        .AddMeter("System.Net.Http")
        .AddPrometheusExporter());

var app = builder.Build();

if (args.Contains("--seed"))
{
    using var seedScope = app.Services.CreateScope();
    var seedDb = seedScope.ServiceProvider.GetRequiredService<OrderFlowDbContext>();
    await seedDb.Database.MigrateAsync();

    var generator = new SeedDataGenerator(
        seedDb,
        seedScope.ServiceProvider.GetRequiredService<ILogger<SeedDataGenerator>>());
    await generator.SeedAsync();
    return;
}

if (app.Configuration.GetValue<bool>("OrderFlow:ApplyMigrationsOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrderFlowDbContext>();
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied");
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapControllers();

// Scrapes and probes are excluded from http.server.* metrics so they do not skew API RED numbers.
if (ObservabilityOptions.From(app.Configuration).Enabled)
{
    app.MapPrometheusScrapingEndpoint("/metrics").DisableHttpMetrics();
}

app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponse })
    .DisableHttpMetrics();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse
}).DisableHttpMetrics();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
}).DisableHttpMetrics();

app.Run();

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    foreach (var entry in report.Entries)
    {
        OrderFlowMetrics.SetHealthCheckStatus(entry.Key, entry.Value.Status);
    }

    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString(),
        totalDurationMs = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            description = entry.Value.Description,
            durationMs = entry.Value.Duration.TotalMilliseconds
        })
    };

    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

public partial class Program;
