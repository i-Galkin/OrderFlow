using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Persistence;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.Processing;

/// <summary>
/// Local stand-in for the warehouse service. Stock lives in Postgres, so a reservation is
/// a decrement of <c>products.StockQuantity</c> plus a reservation row for reconciliation.
/// </summary>
public sealed class FakeInventoryService : IInventoryService
{
    private readonly OrderFlowDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<FakeInventoryService> _logger;

    public FakeInventoryService(
        OrderFlowDbContext db,
        IConnectionMultiplexer redis,
        ILogger<FakeInventoryService> logger)
    {
        _db = db;
        _redis = redis;
        _logger = logger;
    }

    public async Task ReserveAsync(OrderEvent orderEvent, CancellationToken ct = default)
    {
        var productIds = orderEvent.Items.Select(i => i.ProductId).Distinct().ToList();

        var products = await _db.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var item in orderEvent.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                throw new NotFoundException("Product", item.ProductId);
            }

            if (product.StockQuantity < item.Quantity)
            {
                throw new InsufficientStockException(item.ProductId, item.Quantity, product.StockQuantity);
            }
        }

        foreach (var item in orderEvent.Items)
        {
            // Decrement in the database rather than through the tracked entity: the worker
            // only ever moves stock in one direction here and this keeps the statement short.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE products
                 SET "StockQuantity" = "StockQuantity" - {item.Quantity},
                     "Version" = "Version" + 1
                 WHERE "Id" = {item.ProductId}
                 """, ct);

            _db.InventoryReservations.Add(new InventoryReservation
            {
                OrderId = orderEvent.OrderId,
                ProductId = item.ProductId,
                Quantity = item.Quantity
            });

            // Stock changed, so the cached product document is no longer valid.
            await _redis.GetDatabase().KeyDeleteAsync($"product:{item.ProductId}");
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Reserved {LineCount} lines for order {OrderId}",
            orderEvent.Items.Count, orderEvent.OrderId);
    }

    public Task<bool> HasReservationAsync(Guid orderId, CancellationToken ct = default)
        => _db.InventoryReservations.AsNoTracking().AnyAsync(r => r.OrderId == orderId, ct);

    public async Task ReleaseAsync(Guid orderId, CancellationToken ct = default)
    {
        var reservations = await _db.InventoryReservations
            .Where(r => r.OrderId == orderId)
            .ToListAsync(ct);

        foreach (var reservation in reservations)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE products
                 SET "StockQuantity" = "StockQuantity" + {reservation.Quantity},
                     "Version" = "Version" + 1
                 WHERE "Id" = {reservation.ProductId}
                 """, ct);

            await _redis.GetDatabase().KeyDeleteAsync($"product:{reservation.ProductId}");
        }

        _db.InventoryReservations.RemoveRange(reservations);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Released {Count} reservations for order {OrderId}", reservations.Count, orderId);
    }
}
