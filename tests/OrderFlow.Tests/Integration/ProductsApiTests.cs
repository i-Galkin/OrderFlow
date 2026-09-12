using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Tests.Support;
using Xunit;

namespace OrderFlow.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class ProductsApiTests : IDisposable
{
    private readonly OrderFlowApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [PostgresFact]
    public async Task Creating_a_product_returns_201()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest
        {
            Sku = $"SKU-{Guid.NewGuid():N}"[..20],
            Name = "Widget",
            Price = 12.34m,
            StockQuantity = 40
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var product = await response.Content.ReadFromJsonAsync<ProductDto>();
        product!.StockQuantity.Should().Be(40);
    }

    [PostgresFact]
    public async Task Duplicate_skus_are_rejected()
    {
        var client = _factory.CreateClient();
        var sku = $"SKU-{Guid.NewGuid():N}"[..20];

        var request = new CreateProductRequest { Sku = sku, Name = "Widget", Price = 5m, StockQuantity = 1 };
        (await client.PostAsJsonAsync("/api/products", request)).EnsureSuccessStatusCode();

        var duplicate = await client.PostAsJsonAsync("/api/products", request);

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task Invalid_products_are_rejected()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest
        {
            Sku = "",
            Name = "",
            Price = 0m,
            StockQuantity = -1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task Reading_a_product_populates_the_cache()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/products", new CreateProductRequest
        {
            Sku = $"SKU-{Guid.NewGuid():N}"[..20],
            Name = "Cached widget",
            Price = 7.50m,
            StockQuantity = 10
        });
        var product = (await created.Content.ReadFromJsonAsync<ProductDto>())!;

        var response = await client.GetAsync($"/api/products/{product.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Cache.Contains(product.Id).Should().BeTrue();
    }

    [PostgresFact]
    public async Task Unknown_products_return_404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/products/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
