using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Services;

/// <summary>
/// Application service behind the orders API. Owns the write-side transactions.
/// </summary>
public class OrderService
{
    private readonly OrderFlowDbContext _db;
    private readonly ILogger<OrderService> _logger;

    public OrderService(OrderFlowDbContext db, ILogger<OrderService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OrderDto> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == request.CustomerId, ct)
                       ?? throw new NotFoundException("Customer", request.CustomerId);

        var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var items = new List<OrderItem>();
        foreach (var line in request.Items)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                throw new NotFoundException("Product", line.ProductId);
            }

            items.Add(new OrderItem
            {
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitPrice = product.Price
            });
        }

        var order = Order.Create(customer.Id, items);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created order {OrderId} for customer {CustomerId} with {ItemCount} items totalling {TotalAmount}",
            order.Id, order.CustomerId, order.Items.Count, order.TotalAmount);

        order.Customer = customer;
        return order.ToDto();
    }

    public async Task<OrderDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return order?.ToDto();
    }

    public async Task<PagedResult<OrderSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Orders.AsNoTracking();

        var totalCount = await query.CountAsync(ct);

        var orders = await query
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<OrderSummaryDto>
        {
            Items = orders.Select(o => o.ToSummaryDto()).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<OrderDto> ConfirmAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(id, ct);

        order.Confirm();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderId} confirmed", order.Id);
        return order.ToDto();
    }

    public async Task<OrderDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(id, ct);

        order.Cancel();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderId} cancelled", order.Id);
        return order.ToDto();
    }

    public async Task<OrderDto> RetryAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(id, ct);

        if (order.Status != OrderStatus.Failed)
        {
            throw new DomainException($"Order {order.Id} is {order.Status} and cannot be retried.");
        }

        _logger.LogInformation("Retry requested for failed order {OrderId}", order.Id);
        return order.ToDto();
    }

    private async Task<Order> LoadForUpdateAsync(Guid id, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return order ?? throw new NotFoundException("Order", id);
    }
}
