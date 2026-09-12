using FluentAssertions;
using OrderFlow.Infrastructure.Domain;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class ProductStockTests
{
    private static Product NewProduct(int stock) => new()
    {
        Sku = "SKU-TEST-01",
        Name = "Test product",
        Price = 9.99m,
        StockQuantity = stock,
        Version = 1
    };

    [Fact]
    public void Reserve_takes_the_requested_units_out_of_stock()
    {
        var product = NewProduct(10);

        product.Reserve(4);

        product.StockQuantity.Should().Be(6);
        product.Version.Should().Be(2);
    }

    [Fact]
    public void Reserve_refuses_to_go_negative()
    {
        var product = NewProduct(3);

        var act = () => product.Reserve(4);

        act.Should().Throw<InsufficientStockException>()
            .Which.Available.Should().Be(3);
        product.StockQuantity.Should().Be(3);
    }

    [Fact]
    public void Reserving_exactly_the_remaining_stock_is_allowed()
    {
        var product = NewProduct(3);

        product.Reserve(3);

        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void Release_puts_the_units_back()
    {
        var product = NewProduct(10);
        product.Reserve(4);

        product.Release(4);

        product.StockQuantity.Should().Be(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_rejects_non_positive_quantities(int quantity)
    {
        var product = NewProduct(10);

        var act = () => product.Reserve(quantity);

        act.Should().Throw<DomainException>();
    }
}
