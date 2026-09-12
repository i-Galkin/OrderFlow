using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Contracts.Events;
using OrderFlow.Tests.Support;
using Xunit;

namespace OrderFlow.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class OrdersApiTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly OrderFlowApiFactory _factory = new();

    public OrdersApiTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose() => _factory.Dispose();

    private async Task<CreateOrderRequest> BuildRequestAsync(decimal price = 25.00m, int quantity = 1)
    {
        var customer = await _fixture.CreateCustomerAsync();
        var product = await _fixture.CreateProductAsync(price, stock: 100);

        return new CreateOrderRequest
        {
            CustomerId = customer.Id,
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = product.Id, Quantity = quantity }
            }
        };
    }

    [PostgresFact]
    public async Task Creating_an_order_returns_201_and_publishes_an_event()
    {
        var client = _factory.CreateClient();
        var request = await BuildRequestAsync(price: 30m, quantity: 2);

        var response = await client.PostAsJsonAsync("/api/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>();
        order!.Status.Should().Be("Pending");
        order.TotalAmount.Should().Be(60m);
        order.Items.Should().ContainSingle();

        _factory.Publisher.EventsOfType(OrderEventTypes.OrderCreated)
            .Should().Contain(e => e.OrderId == order.Id);
    }

    [PostgresFact]
    public async Task Every_response_carries_a_correlation_id()
    {
        var client = _factory.CreateClient();
        var request = await BuildRequestAsync();

        var response = await client.PostAsJsonAsync("/api/orders", request);

        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task A_caller_supplied_correlation_id_is_echoed_back()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "trace-12345");

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.Headers.GetValues("X-Correlation-ID").Single().Should().Be("trace-12345");
    }

    [PostgresFact]
    public async Task Unknown_orders_return_404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task Orders_without_items_are_rejected()
    {
        var client = _factory.CreateClient();
        var customer = await _fixture.CreateCustomerAsync();

        var response = await client.PostAsJsonAsync("/api/orders", new CreateOrderRequest
        {
            CustomerId = customer.Id,
            Items = new List<CreateOrderItemRequest>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task Quantities_must_be_positive()
    {
        var client = _factory.CreateClient();
        var request = await BuildRequestAsync(quantity: 0);

        var response = await client.PostAsJsonAsync("/api/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task Confirming_an_order_moves_it_to_confirmed()
    {
        var client = _factory.CreateClient();
        var created = await CreateOrderAsync(client);

        var response = await client.PostAsync($"/api/orders/{created.Id}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = await response.Content.ReadFromJsonAsync<OrderDto>();
        confirmed!.Status.Should().Be("Confirmed");

        _factory.Publisher.EventsOfType(OrderEventTypes.OrderConfirmed)
            .Should().Contain(e => e.OrderId == created.Id);
    }

    [PostgresFact]
    public async Task Confirming_twice_is_rejected_with_409()
    {
        var client = _factory.CreateClient();
        var created = await CreateOrderAsync(client);

        await client.PostAsync($"/api/orders/{created.Id}/confirm", null);
        var second = await client.PostAsync($"/api/orders/{created.Id}/confirm", null);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [PostgresFact]
    public async Task Cancelling_a_pending_order_is_accepted()
    {
        var client = _factory.CreateClient();
        var created = await CreateOrderAsync(client);

        var response = await client.PostAsync($"/api/orders/{created.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await response.Content.ReadFromJsonAsync<OrderDto>();
        cancelled!.Status.Should().Be("Cancelled");
    }

    [PostgresFact]
    public async Task Cancelling_a_confirmed_order_is_accepted()
    {
        var client = _factory.CreateClient();
        var created = await CreateOrderAsync(client);
        (await client.PostAsync($"/api/orders/{created.Id}/confirm", null)).EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/api/orders/{created.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await response.Content.ReadFromJsonAsync<OrderDto>();
        cancelled!.Status.Should().Be("Cancelled");

        _factory.Publisher.EventsOfType(OrderEventTypes.OrderCancelled)
            .Should().Contain(e => e.OrderId == created.Id);
    }

    [PostgresFact]
    public async Task Retrying_an_order_that_has_not_failed_is_rejected()
    {
        var client = _factory.CreateClient();
        var created = await CreateOrderAsync(client);

        var response = await client.PostAsync($"/api/orders/{created.Id}/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task Listing_orders_is_paged()
    {
        var client = _factory.CreateClient();
        await CreateOrderAsync(client);

        var response = await client.GetAsync("/api/orders?page=1&pageSize=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<OrderSummaryDto>>();
        page!.PageSize.Should().Be(5);
        page.Items.Should().HaveCountLessThanOrEqualTo(5);
        page.TotalCount.Should().BeGreaterThan(0);
    }

    [PostgresFact]
    public async Task Listing_can_be_filtered_by_status()
    {
        var client = _factory.CreateClient();
        var created = await CreateOrderAsync(client);
        await client.PostAsync($"/api/orders/{created.Id}/cancel", null);

        var response = await client.GetAsync("/api/orders?status=cancelled&pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<OrderSummaryDto>>();
        page!.Items.Should().OnlyContain(o => o.Status == "Cancelled");
    }

    [PostgresFact]
    public async Task An_unknown_status_filter_is_rejected()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/orders?status=teleported");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<OrderDto> CreateOrderAsync(HttpClient client)
    {
        var request = await BuildRequestAsync();
        var response = await client.PostAsJsonAsync("/api/orders", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }
}
