using FluentAssertions;
using OrderFlow.Infrastructure.Seeding;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class SeedDataGeneratorTests
{
    [Fact]
    public void Ids_are_stable_across_runs()
    {
        SeedDataGenerator.DeterministicId("product", 42)
            .Should().Be(SeedDataGenerator.DeterministicId("product", 42));

        SeedDataGenerator.DeterministicId("product", 42)
            .Should().NotBe(SeedDataGenerator.DeterministicId("order", 42));
    }

    [Fact]
    public void Fixture_products_cover_the_payment_scenarios()
    {
        var fixtures = SeedDataGenerator.FixtureProducts().ToList();

        fixtures.Should().Contain(p => p.Sku == "SKU-DECLINE-13" && p.Price == 100.13m);
        fixtures.Should().Contain(p => p.Sku == "SKU-TIMEOUT-77" && p.Price == 100.77m);
        fixtures.Should().Contain(p => p.Sku == "SKU-SCARCE-01" && p.StockQuantity == 5);
        fixtures.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }
}
