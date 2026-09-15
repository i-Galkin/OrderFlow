using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderFlow.Contracts;
using OrderFlow.Contracts.Events;

namespace OrderFlow.Infrastructure.Observability;

/// <summary>
/// Custom business and pipeline metrics for both applications. Static, like
/// <see cref="CorrelationContext"/>, so instrumented services keep their constructors and any
/// code path can record without having a meter threaded through it.
///
/// Every tag here comes from a closed set (status names, event type constants, outcome
/// constants). Never tag with order ids, event ids, correlation ids or exception messages:
/// each distinct value becomes a separate Prometheus series.
/// </summary>
public static class OrderFlowMetrics
{
    public const string MeterName = "OrderFlow";

    private static readonly Meter Meter = new(MeterName, typeof(OrderFlowMetrics).Assembly.GetName().Version?.ToString());

    private static readonly InstrumentAdvice<double> LatencyAdvice = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]
    };

    private static readonly InstrumentAdvice<double> OrderValueAdvice = new()
    {
        HistogramBucketBoundaries = [10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 25_000]
    };

    // API / order lifecycle
    private static readonly Counter<long> OrdersCreated = Meter.CreateCounter<long>(
        "orderflow.orders.created", "{order}", "Orders written by the API.");

    private static readonly Histogram<double> OrderValue = Meter.CreateHistogram(
        "orderflow.orders.value", unit: null, description: "Total amount of created orders.", tags: null, advice: OrderValueAdvice);

    private static readonly Counter<long> OrderStatusChanges = Meter.CreateCounter<long>(
        "orderflow.orders.status_changes", "{order}", "Order status changes persisted, by target status and owning service.");

    private static readonly Counter<long> ManualRetries = Meter.CreateCounter<long>(
        "orderflow.orders.manual_retries", "{order}", "Operator-requested retries of failed orders.");

    private static readonly Counter<long> ApiErrors = Meter.CreateCounter<long>(
        "orderflow.api.errors", "{error}", "Exceptions translated into problem responses by the API.");

    // Kafka producer
    private static readonly Counter<long> MessagesProduced = Meter.CreateCounter<long>(
        "orderflow.kafka.produced", "{message}", "Messages handed to the Kafka producer queue.");

    private static readonly Counter<long> DeliveryReports = Meter.CreateCounter<long>(
        "orderflow.kafka.delivery", "{message}", "Kafka delivery reports, by result.");

    private static readonly Histogram<double> DeliveryDuration = Meter.CreateHistogram(
        "orderflow.kafka.delivery.duration", "s", "Time from Produce to the broker delivery report.", tags: null, advice: LatencyAdvice);

    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "orderflow.kafka.dead_lettered", "{message}", "Events routed to the dead letter topic.");

    private static readonly Counter<long> KafkaClientErrors = Meter.CreateCounter<long>(
        "orderflow.kafka.client_errors", "{error}", "Errors reported by librdkafka client error handlers.");

    // Worker
    private static readonly Counter<long> MessagesHandled = Meter.CreateCounter<long>(
        "orderflow.worker.messages", "{message}", "Consumed messages, by event type and outcome.");

    private static readonly Histogram<double> MessageDuration = Meter.CreateHistogram(
        "orderflow.worker.message.duration", "s", "Wall time spent handling one consumed message, including retry backoff.", tags: null, advice: LatencyAdvice);

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "orderflow.worker.retries", "{retry}", "Worker retry decisions, by attempt and result.");

    private static readonly Histogram<double> RetryBackoff = Meter.CreateHistogram(
        "orderflow.worker.retry.backoff.duration", "s", "Time the consume loop spent waiting in retry backoff.", tags: null, advice: LatencyAdvice);

    private static readonly Counter<long> ConsumeErrors = Meter.CreateCounter<long>(
        "orderflow.worker.consume_errors", "{error}", "Consume calls that threw ConsumeException.");

    private static readonly Counter<long> CorrelationIdsGenerated = Meter.CreateCounter<long>(
        "orderflow.worker.correlation_id.generated", "{message}", "Consumed messages without a recognised correlation header, for which the worker minted a new id.");

    // Processing
    private static readonly Counter<long> DuplicatesSkipped = Meter.CreateCounter<long>(
        "orderflow.processing.duplicates_skipped", "{event}", "Events skipped because processed_events already had them.");

    private static readonly Counter<long> ResumedWithReservation = Meter.CreateCounter<long>(
        "orderflow.processing.resumed_with_reservation", "{order}", "Retried orders completed directly because a reservation already existed.");

    private static readonly Counter<long> MarkProcessedConflicts = Meter.CreateCounter<long>(
        "orderflow.processing.mark_processed_conflicts", "{event}", "processed_events inserts that hit an existing row.");

    private static readonly Counter<long> Payments = Meter.CreateCounter<long>(
        "orderflow.payments", "{payment}", "Payment attempts, by outcome and reason.");

    private static readonly Counter<long> Reservations = Meter.CreateCounter<long>(
        "orderflow.inventory.reservations", "{order}", "Stock reservation attempts, by result.");

    private static readonly Counter<long> Releases = Meter.CreateCounter<long>(
        "orderflow.inventory.releases", "{order}", "Orders whose reservations were released.");

    // Cache
    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>(
        "orderflow.cache.requests", "{request}", "Cache reads, by result.");

    private static readonly Counter<long> CacheWriteErrors = Meter.CreateCounter<long>(
        "orderflow.cache.write_errors", "{error}", "Cache writes that threw.");

    // Gauge state
    private static readonly ConcurrentDictionary<string, double> HealthStatuses = new();
    private static readonly ConcurrentDictionary<(string Topic, int Partition), long> ConsumerLag = new();
    private static long _lastConsumerPollUnixMs;
    private static long _lastLagSampleUnixMs;

    private static readonly Counter<long> LagSampleFailures = Meter.CreateCounter<long>(
        "orderflow.kafka.consumer.lag.sample_failures", "{sample}", "Consumer lag samples that failed.");

    static OrderFlowMetrics()
    {
        Meter.CreateObservableGauge(
            "orderflow.health.check.status",
            ObserveHealthStatuses,
            unit: null,
            description: "Last observed health check result (1 healthy, 0.5 degraded, 0 unhealthy). Updated when a health endpoint is polled.");

        Meter.CreateObservableGauge(
            "orderflow.kafka.consumer.lag",
            ObserveConsumerLag,
            "{message}",
            "Messages between the consumer group's committed offset and the partition end.");

        Meter.CreateObservableGauge(
            "orderflow.kafka.consumer.lag.last_sample",
            () => ObserveTimestamp(ref _lastLagSampleUnixMs),
            "s",
            "Unix time of the last successful consumer lag sample.");

        Meter.CreateObservableGauge(
            "orderflow.worker.last_poll",
            () => ObserveTimestamp(ref _lastConsumerPollUnixMs),
            "s",
            "Unix time the consume loop last polled Kafka. Stops advancing while the loop is blocked.");
    }

    public static class Sources
    {
        public const string Api = "api";
        public const string Worker = "worker";
    }

    public static class MessageOutcomes
    {
        public const string Processed = "processed";
        public const string DuplicateInMemory = "duplicate_in_memory";
        public const string DeadLettered = "dead_lettered";
        public const string RetryScheduled = "retry_scheduled";
        public const string Unparseable = "unparseable";
        public const string Error = "error";
    }

    public static class ReservationResults
    {
        public const string Reserved = "reserved";
        public const string InsufficientStock = "insufficient_stock";
        public const string ProductNotFound = "product_not_found";
    }

    public static class CacheResults
    {
        public const string Hit = "hit";
        public const string Miss = "miss";
        public const string Unavailable = "unavailable";
    }

    public static void RecordOrderCreated(decimal totalAmount)
    {
        OrdersCreated.Add(1);
        OrderValue.Record((double)totalAmount);
    }

    public static void RecordStatusChange(OrderStatus status, string source) =>
        OrderStatusChanges.Add(1, new("status", status.ToString()), new("source", source));

    public static void RecordManualRetry() => ManualRetries.Add(1);

    public static void RecordApiError(Exception exception, int statusCode) =>
        ApiErrors.Add(1, new("type", exception.GetType().Name), new("status_code", statusCode));

    public static void RecordProduced(string topic, string eventType) =>
        MessagesProduced.Add(1, new("topic", topic), new("event_type", NormalizeEventType(eventType)));

    public static void RecordDelivery(string topic, bool delivered, TimeSpan elapsed)
    {
        DeliveryReports.Add(1, new("topic", topic), new("result", delivered ? "delivered" : "error"));
        DeliveryDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("topic", topic));
    }

    public static void RecordDeadLettered(string topic, string eventType) =>
        DeadLettered.Add(1, new("topic", topic), new("event_type", NormalizeEventType(eventType)));

    public static void RecordKafkaClientError(string client, bool fatal) =>
        KafkaClientErrors.Add(1, new("client", client), new("fatal", fatal));

    public static void RecordMessageHandled(string? eventType, string outcome, TimeSpan elapsed)
    {
        var type = NormalizeEventType(eventType);
        MessagesHandled.Add(1, new("event_type", type), new("outcome", outcome));
        MessageDuration.Record(elapsed.TotalSeconds, new("event_type", type), new("outcome", outcome));
    }

    public static void RecordRetry(int attempt, bool exhausted) =>
        Retries.Add(1, new("attempt", attempt), new("result", exhausted ? "exhausted" : "scheduled"));

    public static void RecordRetryBackoff(TimeSpan elapsed) => RetryBackoff.Record(elapsed.TotalSeconds);

    public static void RecordConsumeError() => ConsumeErrors.Add(1);

    public static void RecordCorrelationIdGenerated() => CorrelationIdsGenerated.Add(1);

    public static void MarkConsumerPoll() =>
        Interlocked.Exchange(ref _lastConsumerPollUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static void RecordDuplicateSkipped(string eventType) =>
        DuplicatesSkipped.Add(1, new KeyValuePair<string, object?>("event_type", NormalizeEventType(eventType)));

    public static void RecordResumedWithReservation() => ResumedWithReservation.Add(1);

    public static void RecordMarkProcessedConflict() => MarkProcessedConflicts.Add(1);

    public static void RecordPayment(string outcome, string? reason) =>
        Payments.Add(1, new("outcome", outcome), new("reason", reason ?? "none"));

    public static void RecordReservation(string result) =>
        Reservations.Add(1, new KeyValuePair<string, object?>("result", result));

    public static void RecordRelease() => Releases.Add(1);

    public static void RecordCacheRequest(string cache, string result) =>
        CacheRequests.Add(1, new("cache", cache), new("result", result));

    public static void RecordCacheWriteError(string cache) =>
        CacheWriteErrors.Add(1, new KeyValuePair<string, object?>("cache", cache));

    public static void SetHealthCheckStatus(string check, HealthStatus status) =>
        HealthStatuses[check] = status switch
        {
            HealthStatus.Healthy => 1,
            HealthStatus.Degraded => 0.5,
            _ => 0
        };

    /// <summary>Replaces the lag snapshot for <paramref name="topic"/>.</summary>
    public static void SetConsumerLag(string topic, IReadOnlyDictionary<int, long> lagByPartition)
    {
        foreach (var key in ConsumerLag.Keys.Where(k => k.Topic == topic && !lagByPartition.ContainsKey(k.Partition)))
        {
            ConsumerLag.TryRemove(key, out _);
        }

        foreach (var (partition, lag) in lagByPartition)
        {
            ConsumerLag[(topic, partition)] = lag;
        }

        Interlocked.Exchange(ref _lastLagSampleUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static void RecordLagSampleFailure() => LagSampleFailures.Add(1);

    private static IEnumerable<Measurement<double>> ObserveHealthStatuses() =>
        HealthStatuses.Select(entry => new Measurement<double>(entry.Value, new KeyValuePair<string, object?>("check", entry.Key)));

    private static IEnumerable<Measurement<long>> ObserveConsumerLag() =>
        ConsumerLag.Select(entry => new Measurement<long>(
            entry.Value,
            new KeyValuePair<string, object?>("topic", entry.Key.Topic),
            new KeyValuePair<string, object?>("partition", entry.Key.Partition)));

    private static IEnumerable<Measurement<double>> ObserveTimestamp(ref long unixMs)
    {
        var value = Interlocked.Read(ref unixMs);
        return value == 0 ? [] : [new Measurement<double>(value / 1000d)];
    }

    /// <summary>Event types arrive in message payloads; keep unknown values out of the label set.</summary>
    private static string NormalizeEventType(string? eventType) => eventType switch
    {
        OrderEventTypes.OrderCreated or OrderEventTypes.OrderConfirmed or OrderEventTypes.OrderCancelled
            or OrderEventTypes.OrderProcessingStarted or OrderEventTypes.OrderCompleted
            or OrderEventTypes.OrderFailed => eventType,
        null or "" => "unknown",
        _ => "other"
    };
}
