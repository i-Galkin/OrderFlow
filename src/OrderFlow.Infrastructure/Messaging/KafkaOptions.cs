namespace OrderFlow.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = "orders.events";

    public string DeadLetterTopic { get; set; } = "orders.failed";

    public string ConsumerGroupId { get; set; } = "orderflow-worker";

    /// <summary>Milliseconds the producer waits to batch messages before sending.</summary>
    public int LingerMs { get; set; } = 500;

    /// <summary>Maximum messages buffered in the producer queue before Produce blocks.</summary>
    public int QueueBufferingMaxMessages { get; set; } = 500_000;

    public int MaxRetryAttempts { get; set; } = 3;

    public int RetryBaseDelaySeconds { get; set; } = 2;

    public int SessionTimeoutMs { get; set; } = 10_000;

    public int MaxPollIntervalMs { get; set; } = 300_000;
}
