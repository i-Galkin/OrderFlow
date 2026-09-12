using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Observability;

namespace OrderFlow.Infrastructure.Messaging;

public static class OrderEventFactory
{
    public static OrderEvent From(Order order, string eventType, int attempt = 0, string? reason = null)
    {
        return new OrderEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            OrderId = order.Id,
            CustomerId = order.CustomerId,
            TotalAmount = order.TotalAmount,
            OccurredAt = DateTime.UtcNow,
            CorrelationId = CorrelationContext.CorrelationId,
            Attempt = attempt,
            Reason = reason,
            Items = order.Items.Select(i => new OrderEventItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };
    }
}
