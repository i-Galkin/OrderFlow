using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Seeding;

/// <summary>
/// Builds the local development data set. Everything is derived from the row index, so two
/// runs on two machines produce byte-for-byte identical ids, amounts and timestamps.
/// </summary>
public sealed class SeedDataGenerator
{
    public const int CustomerCount = 100;
    public const int ProductCount = 10_000;
    public const int OrderCount = 50_000;

    /// <summary>Anchor for every generated timestamp so the data set never shifts.</summary>
    public static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly OrderFlowDbContext _db;
    private readonly ILogger<SeedDataGenerator> _logger;

    public SeedDataGenerator(OrderFlowDbContext db, ILogger<SeedDataGenerator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public static Guid DeterministicId(string kind, int index)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"orderflow:{kind}:{index}"));
        return new Guid(bytes);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Customers.AnyAsync(ct))
        {
            _logger.LogInformation("Seed data already present, nothing to do");
            return;
        }

        _db.ChangeTracker.AutoDetectChangesEnabled = false;

        await SeedCustomersAsync(ct);
        await SeedProductsAsync(ct);
        await SeedOrdersAsync(ct);

        _db.ChangeTracker.AutoDetectChangesEnabled = true;
        _logger.LogInformation("Seeding complete");
    }

    private async Task SeedCustomersAsync(CancellationToken ct)
    {
        var customers = new List<Customer>(CustomerCount);
        for (var i = 0; i < CustomerCount; i++)
        {
            customers.Add(new Customer
            {
                Id = DeterministicId("customer", i),
                Email = $"customer{i:D3}@orderflow.test",
                Name = $"Customer {i:D3}",
                CreatedAt = Epoch.AddHours(i)
            });
        }

        // Fixture account used by the QA suite.
        customers[0].Email = "qa.automation@orderflow.test";
        customers[0].Name = "QA Automation";

        _db.Customers.AddRange(customers);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        _logger.LogInformation("Seeded {Count} customers", customers.Count);
    }

    private async Task SeedProductsAsync(CancellationToken ct)
    {
        var products = new List<Product>(ProductCount);
        for (var i = 0; i < ProductCount; i++)
        {
            // Prices run from 5.00 to 504.99 in a repeating pattern.
            var price = 5m + (i % 500) + (i % 100) / 100m;

            products.Add(new Product
            {
                Id = DeterministicId("product", i),
                Sku = $"SKU-{i:D6}",
                Name = $"Product {i:D6}",
                Price = decimal.Round(price, 2),
                StockQuantity = 20 + (i % 180),
                Version = 1
            });
        }

        foreach (var fixture in FixtureProducts())
        {
            products.Add(fixture);
        }

        const int batchSize = 2_000;
        for (var offset = 0; offset < products.Count; offset += batchSize)
        {
            _db.Products.AddRange(products.Skip(offset).Take(batchSize));
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation("Seeded {Count} products", products.Count);
    }

    /// <summary>
    /// Products with amounts the payment stub treats specially, plus a deliberately scarce
    /// item used by the stock tests.
    /// </summary>
    public static IEnumerable<Product> FixtureProducts()
    {
        yield return new Product
        {
            Id = DeterministicId("fixture-product", 1),
            Sku = "SKU-DECLINE-13",
            Name = "Fixture: declined card",
            Price = 100.13m,
            StockQuantity = 5_000,
            Version = 1
        };

        yield return new Product
        {
            Id = DeterministicId("fixture-product", 2),
            Sku = "SKU-TIMEOUT-77",
            Name = "Fixture: gateway timeout",
            Price = 100.77m,
            StockQuantity = 5_000,
            Version = 1
        };

        yield return new Product
        {
            Id = DeterministicId("fixture-product", 3),
            Sku = "SKU-SCARCE-01",
            Name = "Fixture: scarce stock",
            Price = 49.50m,
            StockQuantity = 5,
            Version = 1
        };

        yield return new Product
        {
            Id = DeterministicId("fixture-product", 4),
            Sku = "SKU-HIGHVALUE-01",
            Name = "Fixture: high value",
            Price = 12_500.00m,
            StockQuantity = 500,
            Version = 1
        };
    }

    private async Task SeedOrdersAsync(CancellationToken ct)
    {
        var customerIds = Enumerable.Range(0, CustomerCount)
            .Select(i => DeterministicId("customer", i))
            .ToArray();

        const int batchSize = 2_000;
        var orders = new List<Order>(batchSize);

        for (var i = 0; i < OrderCount; i++)
        {
            var order = new Order
            {
                Id = DeterministicId("order", i),
                CustomerId = customerIds[i % CustomerCount],
                Status = StatusFor(i),
                CreatedAt = Epoch.AddMinutes(i * 5),
                UpdatedAt = Epoch.AddMinutes(i * 5 + 30),
                Version = 1
            };

            var lineCount = (i % 3) + 1;
            for (var line = 0; line < lineCount; line++)
            {
                var productIndex = (i * 7 + line * 13) % ProductCount;
                var unitPrice = decimal.Round(5m + (productIndex % 500) + (productIndex % 100) / 100m, 2);

                order.Items.Add(new OrderItem
                {
                    Id = DeterministicId("order-item", i * 4 + line),
                    OrderId = order.Id,
                    ProductId = DeterministicId("product", productIndex),
                    Quantity = (line % 2) + 1,
                    UnitPrice = unitPrice
                });
            }

            order.TotalAmount = order.Items.Sum(item => item.UnitPrice * item.Quantity);
            if (order.Status == OrderStatus.Failed)
            {
                order.FailureReason = "card_declined";
            }

            orders.Add(order);

            if (orders.Count == batchSize)
            {
                await FlushOrdersAsync(orders, ct);
            }
        }

        if (orders.Count > 0)
        {
            await FlushOrdersAsync(orders, ct);
        }

        _logger.LogInformation("Seeded {Count} orders", OrderCount);
    }

    private async Task FlushOrdersAsync(List<Order> orders, CancellationToken ct)
    {
        _db.Orders.AddRange(orders);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        orders.Clear();
    }

    private static OrderStatus StatusFor(int index) => (index % 20) switch
    {
        < 6 => OrderStatus.Pending,
        < 8 => OrderStatus.Confirmed,
        < 9 => OrderStatus.Processing,
        < 18 => OrderStatus.Completed,
        < 19 => OrderStatus.Cancelled,
        _ => OrderStatus.Failed
    };
}
