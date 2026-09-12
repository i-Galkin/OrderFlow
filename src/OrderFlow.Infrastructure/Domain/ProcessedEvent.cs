namespace OrderFlow.Infrastructure.Domain;

/// <summary>
/// Deduplication ledger for consumed Kafka events. Kafka gives us at-least-once delivery,
/// so the worker records every event it has handled and skips repeats.
/// </summary>
public class ProcessedEvent
{
    public Guid EventId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public Guid OrderId { get; set; }

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Stock taken out of the catalogue for a specific order. Kept separate from the order
/// items so that a release can be reconciled independently of the order lifecycle.
/// </summary>
public class InventoryReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrderId { get; set; }

    public Guid ProductId { get; set; }

    public int Quantity { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
