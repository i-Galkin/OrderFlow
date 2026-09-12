using OrderFlow.Contracts;

namespace OrderFlow.Infrastructure.Domain;

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Order> Orders { get; set; } = new();
}

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int StockQuantity { get; set; }

    /// <summary>Optimistic concurrency token, bumped by every write that touches stock.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Reserves stock for an order. Callers are responsible for persisting within a
    /// transaction that also records the reservation.
    /// </summary>
    public void Reserve(int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException($"Reservation quantity for product {Id} must be positive.");
        }

        if (StockQuantity < quantity)
        {
            throw new InsufficientStockException(Id, quantity, StockQuantity);
        }

        StockQuantity -= quantity;
        Version++;
    }

    public void Release(int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException($"Release quantity for product {Id} must be positive.");
        }

        StockQuantity += quantity;
        Version++;
    }
}

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrderId { get; set; }

    public Guid ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public Product? Product { get; set; }

    public decimal LineTotal => UnitPrice * Quantity;
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerId { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public decimal TotalAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? FailureReason { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public int Version { get; set; }

    public Customer? Customer { get; set; }

    public List<OrderItem> Items { get; set; } = new();

    public static Order Create(Guid customerId, IEnumerable<OrderItem> items)
    {
        var order = new Order
        {
            CustomerId = customerId,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        foreach (var item in items)
        {
            item.OrderId = order.Id;
            order.Items.Add(item);
        }

        if (order.Items.Count == 0)
        {
            throw new DomainException("An order must contain at least one item.");
        }

        order.TotalAmount = order.Items.Sum(i => i.LineTotal);
        return order;
    }

    public void Confirm() => TransitionTo(OrderStatus.Confirmed);

    public void Cancel() => TransitionTo(OrderStatus.Cancelled);

    public void StartProcessing() => TransitionTo(OrderStatus.Processing);

    public void Complete()
    {
        TransitionTo(OrderStatus.Completed);
        FailureReason = null;
    }

    public void Fail(string reason)
    {
        TransitionTo(OrderStatus.Failed);
        FailureReason = reason;
    }

    private void TransitionTo(OrderStatus target)
    {
        if (!OrderStatusTransitions.CanTransition(Status, target))
        {
            throw new InvalidStatusTransitionException(Id, Status.ToString(), target.ToString());
        }

        Status = target;
        UpdatedAt = DateTime.UtcNow;
        Version++;
    }
}
