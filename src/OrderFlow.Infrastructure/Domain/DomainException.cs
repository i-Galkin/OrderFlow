namespace OrderFlow.Infrastructure.Domain;

/// <summary>
/// Thrown when a caller tries to move the domain into a state it does not allow.
/// Mapped to HTTP 409 by the API.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}

public sealed class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(Guid orderId, string from, string to)
        : base($"Order {orderId} cannot move from {from} to {to}.")
    {
        OrderId = orderId;
        From = from;
        To = to;
    }

    public Guid OrderId { get; }

    public string From { get; }

    public string To { get; }
}

public sealed class InsufficientStockException : DomainException
{
    public InsufficientStockException(Guid productId, int requested, int available)
        : base($"Product {productId} has {available} in stock, {requested} requested.")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }

    public Guid ProductId { get; }

    public int Requested { get; }

    public int Available { get; }
}
