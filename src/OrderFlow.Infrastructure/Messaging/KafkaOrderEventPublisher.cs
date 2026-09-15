using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Observability;

namespace OrderFlow.Infrastructure.Messaging;

/// <summary>
/// Publishes order events to the single <c>orders.events</c> topic, keyed by order id so
/// that all events for one order land on the same partition and stay ordered.
/// </summary>
public sealed class KafkaOrderEventPublisher : IOrderEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaOrderEventPublisher> _logger;

    public KafkaOrderEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaOrderEventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = _options.LingerMs,
            QueueBufferingMaxMessages = _options.QueueBufferingMaxMessages,
            CompressionType = CompressionType.Snappy,
            MessageSendMaxRetries = 5
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
            {
                OrderFlowMetrics.RecordKafkaClientError("producer", error.IsFatal);
                _logger.LogWarning("Kafka producer error {Code}: {Reason}", error.Code, error.Reason);
            })
            .Build();
    }

    public Task PublishAsync(OrderEvent orderEvent, CancellationToken ct = default)
        => ProduceAsync(_options.Topic, orderEvent, ct);

    public Task PublishToDeadLetterAsync(OrderEvent orderEvent, string reason, CancellationToken ct = default)
    {
        orderEvent.Reason = reason;
        _logger.LogWarning(
            "Routing event {EventId} ({EventType}) for order {OrderId} to {Topic}: {Reason}",
            orderEvent.EventId, orderEvent.EventType, orderEvent.OrderId, _options.DeadLetterTopic, reason);

        OrderFlowMetrics.RecordDeadLettered(_options.DeadLetterTopic, orderEvent.EventType);

        return ProduceAsync(_options.DeadLetterTopic, orderEvent, ct);
    }

    private Task ProduceAsync(string topic, OrderEvent orderEvent, CancellationToken ct)
    {
        orderEvent.CorrelationId ??= CorrelationContext.CorrelationId;

        var message = new Message<string, string>
        {
            Key = orderEvent.OrderId.ToString(),
            Value = JsonSerializer.Serialize(orderEvent, SerializerOptions),
            Headers = new Headers
            {
                { CorrelationContext.KafkaHeaderName, System.Text.Encoding.UTF8.GetBytes(orderEvent.CorrelationId ?? string.Empty) },
                { "event-type", System.Text.Encoding.UTF8.GetBytes(orderEvent.EventType) }
            }
        };

        var produceStarted = Stopwatch.GetTimestamp();
        _producer.Produce(topic, message, report =>
        {
            OrderFlowMetrics.RecordDelivery(topic, !report.Error.IsError, Stopwatch.GetElapsedTime(produceStarted));

            if (report.Error.IsError)
            {
                _logger.LogError(
                    "Failed to deliver event {EventId} for order {OrderId}: {Reason}",
                    orderEvent.EventId, orderEvent.OrderId, report.Error.Reason);
            }
        });

        OrderFlowMetrics.RecordProduced(topic, orderEvent.EventType);

        _logger.LogInformation(
            "Published {EventType} for order {OrderId} (event {EventId}, attempt {Attempt})",
            orderEvent.EventType, orderEvent.OrderId, orderEvent.EventId, orderEvent.Attempt);

        return Task.CompletedTask;
    }
}
