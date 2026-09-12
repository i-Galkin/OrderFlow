namespace OrderFlow.Contracts;

/// <summary>
/// Lifecycle of an order. Persisted as an int, so the numeric values are part of
/// the database contract and must not be reordered.
/// </summary>
public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Processing = 2,
    Completed = 3,
    Cancelled = 4,
    Failed = 5
}
