using OrderFlow.Contracts;

namespace OrderFlow.Infrastructure.Domain;

/// <summary>
/// Single source of truth for the order state machine.
/// </summary>
public static class OrderStatusTransitions
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.Pending] = new[] { OrderStatus.Confirmed, OrderStatus.Cancelled },
        [OrderStatus.Confirmed] = new[] { OrderStatus.Processing },
        [OrderStatus.Processing] = new[] { OrderStatus.Completed, OrderStatus.Failed },
        [OrderStatus.Failed] = new[] { OrderStatus.Processing },
        [OrderStatus.Completed] = Array.Empty<OrderStatus>(),
        [OrderStatus.Cancelled] = Array.Empty<OrderStatus>()
    };

    public static bool CanTransition(OrderStatus from, OrderStatus to)
        => Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyCollection<OrderStatus> AllowedFrom(OrderStatus from)
        => Allowed.TryGetValue(from, out var targets) ? targets : Array.Empty<OrderStatus>();

    public static bool IsTerminal(OrderStatus status)
        => AllowedFrom(status).Count == 0;
}
