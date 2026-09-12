namespace OrderFlow.Infrastructure.Processing;

/// <summary>
/// Marks a failure that is worth retrying (gateway timeouts, broker hiccups, deadlocks).
/// Anything else is considered permanent and goes straight to the dead letter topic.
/// </summary>
public sealed class TransientProcessingException : Exception
{
    public TransientProcessingException(string reason) : base(reason)
    {
    }

    public TransientProcessingException(string reason, Exception inner) : base(reason, inner)
    {
    }
}
