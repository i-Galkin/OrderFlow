using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Observability;
using OrderFlow.Infrastructure.Processing;

namespace OrderFlow.Worker;

/// <summary>
/// Consumes <c>orders.events</c> and drives confirmed orders through the processing
/// pipeline. Failures are retried with a backoff by re-publishing the event; once the
/// retry budget is exhausted the event is routed to <c>orders.failed</c>.
/// </summary>
public sealed class OrderEventConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOrderEventPublisher _publisher;
    private readonly KafkaOptions _options;
    private readonly ILogger<OrderEventConsumer> _logger;

    /// <summary>Retry budget consumed so far, per order.</summary>
    private readonly Dictionary<Guid, int> _attempts = new();

    /// <summary>Events handled by this instance, used to short-circuit obvious redeliveries.</summary>
    private readonly HashSet<Guid> _handledEvents = new();

    public OrderEventConsumer(
        IServiceScopeFactory scopeFactory,
        IOrderEventPublisher publisher,
        IOptions<KafkaOptions> options,
        ILogger<OrderEventConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            SessionTimeoutMs = _options.SessionTimeoutMs,
            MaxPollIntervalMs = _options.MaxPollIntervalMs
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
                _logger.LogWarning("Kafka consumer error {Code}: {Reason}", error.Code, error.Reason))
            .Build();

        consumer.Subscribe(_options.Topic);
        _logger.LogInformation("Subscribed to {Topic} as group {GroupId}", _options.Topic, _options.ConsumerGroupId);

        // One scope for the lifetime of the consumer: the pipeline is single threaded, so
        // the services it resolves can be reused across messages.
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OrderEventProcessor>();

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result;
            try
            {
                result = consumer.Consume(TimeSpan.FromSeconds(1));
            }
            catch (ConsumeException ex)
            {
                _logger.LogWarning(ex, "Consume failed: {Reason}", ex.Error.Reason);
                continue;
            }

            if (result?.Message is null)
            {
                continue;
            }

            await HandleMessageAsync(consumer, processor, result, stoppingToken);
        }

        consumer.Close();
    }

    private async Task HandleMessageAsync(
        IConsumer<string, string> consumer,
        OrderEventProcessor processor,
        ConsumeResult<string, string> result,
        CancellationToken ct)
    {
        var correlationId = ReadCorrelationId(result.Message.Headers) ?? Guid.NewGuid().ToString("N");
        using var correlationScope = CorrelationContext.BeginScope(correlationId);
        using var loggerScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["Topic"] = result.Topic,
            ["Partition"] = result.Partition.Value,
            ["Offset"] = result.Offset.Value
        });

        OrderEvent? orderEvent = null;
        try
        {
            orderEvent = JsonSerializer.Deserialize<OrderEvent>(result.Message.Value, SerializerOptions);
            if (orderEvent is null)
            {
                throw new InvalidOperationException("Event payload deserialized to null.");
            }

            if (_handledEvents.Contains(orderEvent.EventId))
            {
                _logger.LogInformation("Event {EventId} already handled by this instance", orderEvent.EventId);
                consumer.Commit(result);
                return;
            }

            await processor.ProcessAsync(orderEvent, ct);

            _handledEvents.Add(orderEvent.EventId);
            consumer.Commit(result);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Permanent failure for order {OrderId}", orderEvent?.OrderId);
            if (orderEvent is not null)
            {
                await _publisher.PublishToDeadLetterAsync(orderEvent, ex.Message, ct);
                consumer.Commit(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Processing failed for order {OrderId}, scheduling retry",
                orderEvent?.OrderId);

            if (orderEvent is not null)
            {
                await ScheduleRetryAsync(orderEvent, ex.Message, ct);
                consumer.Commit(result);
            }
        }
    }

    private async Task ScheduleRetryAsync(OrderEvent orderEvent, string reason, CancellationToken ct)
    {
        var attempt = _attempts.TryGetValue(orderEvent.OrderId, out var previous) ? previous + 1 : 1;
        _attempts[orderEvent.OrderId] = attempt;

        if (attempt > _options.MaxRetryAttempts)
        {
            await _publisher.PublishToDeadLetterAsync(orderEvent, reason, ct);
            _attempts.Remove(orderEvent.OrderId);
            return;
        }

        var backoff = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * attempt);
        _logger.LogInformation(
            "Retry {Attempt}/{MaxAttempts} for order {OrderId} in {Backoff}s",
            attempt, _options.MaxRetryAttempts, orderEvent.OrderId, backoff.TotalSeconds);

        await Task.Delay(backoff, ct);

        await _publisher.PublishAsync(new OrderEvent
        {
            EventId = Guid.NewGuid(),
            EventType = orderEvent.EventType,
            OrderId = orderEvent.OrderId,
            CustomerId = orderEvent.CustomerId,
            TotalAmount = orderEvent.TotalAmount,
            OccurredAt = DateTime.UtcNow,
            CorrelationId = orderEvent.CorrelationId,
            Attempt = attempt,
            Reason = reason,
            Items = orderEvent.Items
        }, ct);
    }

    private static string? ReadCorrelationId(Headers? headers)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "correlationId", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8.GetString(header.GetValueBytes());
            }
        }

        return null;
    }
}
