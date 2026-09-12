using OrderFlow.Contracts.Events;

namespace OrderFlow.Infrastructure.Processing;

public interface IInventoryService
{
    /// <summary>
    /// Takes the ordered quantities out of the catalogue. Throws
    /// <see cref="Domain.InsufficientStockException"/> when a line cannot be satisfied.
    /// </summary>
    Task ReserveAsync(OrderEvent orderEvent, CancellationToken ct = default);

    Task<bool> HasReservationAsync(Guid orderId, CancellationToken ct = default);

    Task ReleaseAsync(Guid orderId, CancellationToken ct = default);
}
