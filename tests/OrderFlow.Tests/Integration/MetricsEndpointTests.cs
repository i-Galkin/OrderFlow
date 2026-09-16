using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Infrastructure.Observability;
using OrderFlow.Tests.Support;
using Xunit;

namespace OrderFlow.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class MetricsEndpointTests : IDisposable
{
    private readonly OrderFlowApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [PostgresFact]
    public async Task Metrics_endpoint_exposes_http_and_orderflow_series()
    {
        var client = _factory.CreateClient();
        var request = new CreateProductRequest
        {
            Sku = $"SKU-{Guid.NewGuid():N}"[..20],
            Name = "Metrics widget",
            Price = 3m,
            StockQuantity = 1
        };

        (await client.PostAsJsonAsync("/api/products", request)).EnsureSuccessStatusCode();
        // Duplicate SKU -> DomainException -> counted by the exception middleware.
        (await client.PostAsJsonAsync("/api/products", request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("http_server_request_duration_seconds");
        body.Should().Contain("orderflow_api_errors_total");
    }

    [PostgresFact]
    public async Task Scrapes_and_probes_are_not_counted_as_api_traffic()
    {
        var client = _factory.CreateClient();

        await client.GetAsync("/health/live");
        await client.GetAsync("/metrics");
        var body = await (await client.GetAsync("/metrics")).Content.ReadAsStringAsync();

        body.Should().NotContain("http_route=\"/metrics\"");
        body.Should().NotContain("http_route=\"/health/live\"");
    }

    [PostgresFact]
    public async Task Exports_prometheus_metric_names_that_alerts_depend_on()
    {
        var client = _factory.CreateClient();

        // Set the gauge directly rather than via /health/ready: the test host has no Redis override,
        // so the checks may not run here, and this test is about the exported name, not the checks.
        var check = $"test-{Guid.NewGuid():N}";
        OrderFlowMetrics.SetHealthCheckStatus(check, HealthStatus.Healthy);

        // Confirming an unknown order throws NotFoundException, which the exception middleware
        // counts. An unmatched route would 404 without ever reaching it.
        (await client.PostAsync($"/api/orders/{Guid.NewGuid()}/confirm", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n');

        // Assert labelled samples, not just the HELP/TYPE lines an instrument with no points emits.
        lines.Should().Contain(l => l.StartsWith("orderflow_health_check_status{") && l.Contains($"check=\"{check}\""));
        lines.Should().Contain(l => l.StartsWith("orderflow_api_errors_total{")
            && l.Contains("status_code=\"404\"") && l.Contains("type=\"NotFoundException\""));
        lines.Should().Contain(l => l.StartsWith("http_server_request_duration_seconds_count{"));
    }
}
