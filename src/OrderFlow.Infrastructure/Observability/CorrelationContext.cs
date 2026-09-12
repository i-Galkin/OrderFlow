namespace OrderFlow.Infrastructure.Observability;

/// <summary>
/// Ambient correlation id for the current logical operation (an HTTP request in the API,
/// a consumed message in the worker). Flows across awaits via <see cref="AsyncLocal{T}"/>
/// so publishers and repositories do not have to thread it through every signature.
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>Inbound/outbound HTTP header.</summary>
    public const string HttpHeaderName = "X-Correlation-ID";

    /// <summary>Kafka message header used when publishing order events.</summary>
    public const string KafkaHeaderName = "correlation-id";

    public static string? CorrelationId
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string GetOrCreate()
    {
        var existing = Current.Value;
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var generated = Guid.NewGuid().ToString("N");
        Current.Value = generated;
        return generated;
    }

    public static IDisposable BeginScope(string correlationId)
    {
        var previous = Current.Value;
        Current.Value = correlationId;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;

        public Restore(string? previous) => _previous = previous;

        public void Dispose() => Current.Value = _previous;
    }
}
