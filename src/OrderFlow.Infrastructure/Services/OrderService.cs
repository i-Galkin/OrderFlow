using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Observability;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Services;

/// <summary>
/// Application service behind the orders API. Owns the write-side transactions.
/// </summary>
public class OrderService
{
    private readonly OrderFlowDbContext _db;
    private readonly IOrderEventPublisher _publisher;
    private readonly ILogger<OrderService> _logger;

    public OrderService(OrderFlowDbContext db, IOrderEventPublisher publisher, ILogger<OrderService> logger)
    {
        _db = db;
        _publisher = publisher;
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

        // Emit OrderCreated as part of the same logical operation: consumers of the topic
        // should learn about the order at the moment it is written.
        await _publisher.PublishAsync(OrderEventFactory.From(order, OrderEventTypes.OrderCreated), ct);

        await _db.SaveChangesAsync(ct);
        OrderFlowMetrics.RecordOrderCreated(order.TotalAmount);

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

    public async Task<PagedResult<OrderSummaryDto>> ListAsync(
        int page,
        int pageSize,
        OrderStatus? status,
        CancellationToken ct)
    {
        if (status is not null)
        {
            return await ListByStatusAsync(page, pageSize, status.Value, ct);
        }

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

    /// <summary>
    /// Status filtered listing. Operations use this to work through a backlog, so the
    /// summaries have to include the item counts, which means the items travel with the
    /// rows and the page is cut once the result set is in hand.
    /// </summary>
    private async Task<PagedResult<OrderSummaryDto>> ListByStatusAsync(
        int page,
        int pageSize,
        OrderStatus status,
        CancellationToken ct)
    {
        var matching = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.Status == status)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

        var items = matching
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => o.ToSummaryDto())
            .ToList();

        return new PagedResult<OrderSummaryDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = matching.Count
        };
    }

    public async Task<OrderDto> ConfirmAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(id, ct);

        order.Confirm();
        await _db.SaveChangesAsync(ct);
        OrderFlowMetrics.RecordStatusChange(OrderStatus.Confirmed, OrderFlowMetrics.Sources.Api);

        await _publisher.PublishAsync(OrderEventFactory.From(order, OrderEventTypes.OrderConfirmed), ct);

        _logger.LogInformation("Order {OrderId} confirmed", order.Id);
        return order.ToDto();
    }

    public async Task<OrderDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(id, ct);

        if (order.Status == OrderStatus.Confirmed)
        {
            // Support has to be able to pull an order back after it has been confirmed but
            // before the worker has started on it; the worker releases the stock when it
            // sees OrderCancelled.
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            order.Cancel();
        }

        await _db.SaveChangesAsync(ct);
        OrderFlowMetrics.RecordStatusChange(OrderStatus.Cancelled, OrderFlowMetrics.Sources.Api);

        await _publisher.PublishAsync(OrderEventFactory.From(order, OrderEventTypes.OrderCancelled), ct);

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

        // Re-publishing the confirmation is what kicks the worker off again; the worker owns
        // the Failed -> Processing transition.
        await _publisher.PublishAsync(
            OrderEventFactory.From(order, OrderEventTypes.OrderConfirmed, attempt: 1, reason: "manual-retry"),
            ct);
        OrderFlowMetrics.RecordManualRetry();

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
