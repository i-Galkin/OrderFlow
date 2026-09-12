namespace OrderFlow.Contracts.Events;

/// <summary>
/// Every order lifecycle event travels on the single <c>orders.events</c> topic as one
/// envelope. Keeping a single schema means consumers never need a type registry, and the
/// partition key (order id) keeps per-order ordering.
/// </summary>
public sealed class OrderEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;

    public Guid OrderId { get; set; }

    public Guid CustomerId { get; set; }

    public decimal TotalAmount { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public string? CorrelationId { get; set; }

    /// <summary>Delivery attempt, incremented when the worker re-publishes for retry.</summary>
    public int Attempt { get; set; }

    public string? Reason { get; set; }

    public List<OrderEventItem> Items { get; set; } = new();
}

public sealed class OrderEventItem
{
    public Guid ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }
}

public static class OrderEventTypes
{
    public const string OrderCreated = "OrderCreated";
    public const string OrderConfirmed = "OrderConfirmed";
    public const string OrderCancelled = "OrderCancelled";
    public const string OrderProcessingStarted = "OrderProcessingStarted";
    public const string OrderCompleted = "OrderCompleted";
    public const string OrderFailed = "OrderFailed";
}

public static class KafkaTopics
{
    public const string OrderEvents = "orders.events";
    public const string OrderEventsDeadLetter = "orders.failed";
}
