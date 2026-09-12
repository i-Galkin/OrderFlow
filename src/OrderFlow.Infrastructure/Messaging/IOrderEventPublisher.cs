using OrderFlow.Contracts.Events;

namespace OrderFlow.Infrastructure.Messaging;

public interface IOrderEventPublisher
{
    Task PublishAsync(OrderEvent orderEvent, CancellationToken ct = default);

    Task PublishToDeadLetterAsync(OrderEvent orderEvent, string reason, CancellationToken ct = default);
}
