using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderFlow.Contracts.Dtos;
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

        // Ensure health check status has been recorded at least once
        await client.GetAsync("/health/ready");

        // Ensure API error has been recorded at least once (404 from unknown endpoint)
        await client.GetAsync("/api/nonexistent");

        var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // Alert rules depend on these Prometheus metric names existing
        body.Should().Contain("orderflow_health_check_status");
        body.Should().Contain("orderflow_api_errors_total");
        body.Should().Contain("http_server_request_duration_seconds");
    }
}
