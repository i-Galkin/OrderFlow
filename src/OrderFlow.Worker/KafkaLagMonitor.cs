using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Observability;

namespace OrderFlow.Worker;

/// <summary>
/// Samples consumer group lag for <c>orders.events</c> with its own admin client, independently
/// of <see cref="OrderEventConsumer"/>, so lag stays accurate while the consume loop is blocked.
/// Read-only: it fetches committed and end offsets and never joins the group. Failures are logged
/// and counted; the monitor never stops the host.
/// </summary>
public sealed class KafkaLagMonitor : BackgroundService
{
    /// <summary>Kept short: a sample blocked on an unreachable broker delays worker shutdown by up to this much.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly KafkaOptions _kafka;
    private readonly ObservabilityOptions _observability;
    private readonly ILogger<KafkaLagMonitor> _logger;

    public KafkaLagMonitor(
        IOptions<KafkaOptions> kafka,
        IOptions<ObservabilityOptions> observability,
        ILogger<KafkaLagMonitor> logger)
    {
        _kafka = kafka.Value;
        _observability = observability.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_observability.Enabled || _observability.ConsumerLagPollSeconds <= 0)
        {
            return;
        }

        await Task.Yield();

        using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = _kafka.BootstrapServers,
                ClientId = $"{_kafka.ConsumerGroupId}-lag-monitor"
            })
            .SetErrorHandler((_, error) => OrderFlowMetrics.RecordKafkaClientError("lag_monitor", error.IsFatal))
            .Build();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_observability.ConsumerLagPollSeconds));

        try
        {
            do
            {
                try
                {
                    await SampleAsync(admin);
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down; a sample cut short is not a failure worth reporting.
                    return;
                }
                catch (Exception ex)
                {
                    OrderFlowMetrics.RecordLagSampleFailure();
                    _logger.LogWarning(ex, "Consumer lag sample for {Topic} failed", _kafka.Topic);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SampleAsync(IAdminClient admin)
    {
        // Cluster-wide metadata rather than a per-topic request, so sampling can never trigger
        // broker-side topic auto-creation.
        var topic = admin.GetMetadata(RequestTimeout).Topics.FirstOrDefault(t => t.Topic == _kafka.Topic);
        if (topic is null)
        {
            // Simply absent (e.g. not created yet): report zero lag rather than a failed sample.
            OrderFlowMetrics.SetConsumerLag(_kafka.Topic, new Dictionary<int, long>());
            return;
        }

        if (topic.Error.IsError && topic.Error.Code != ErrorCode.UnknownTopicOrPart)
        {
            // A metadata error (e.g. LeaderNotAvailable) is a failed sample, not "no lag": don't
            // clear the series and don't refresh the last-sample gauge, so ConsumerLagMonitorStale
            // can still fire. Thrown so the caller's existing failed-sample handling applies.
            // UnknownTopicOrPart is treated as simply absent, same as the null case above: it is
            // usually how an absent topic actually comes back from cluster-wide metadata, often
            // with an empty partition list too, which the check below alone would not catch.
            throw new KafkaException(topic.Error);
        }

        if (topic.Partitions.Count == 0)
        {
            // Simply absent (e.g. not created yet): report zero lag rather than a failed sample.
            OrderFlowMetrics.SetConsumerLag(_kafka.Topic, new Dictionary<int, long>());
            return;
        }

        var partitions = topic.Partitions
            .Select(p => new TopicPartition(_kafka.Topic, new Partition(p.PartitionId)))
            .ToList();

        var committedResults = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(_kafka.ConsumerGroupId, partitions)],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = RequestTimeout });

        var committed = committedResults
            .SelectMany(r => r.Partitions)
            .ToDictionary(p => p.Partition.Value, p => p.Offset.Value);

        var latest = await ListOffsetsAsync(admin, partitions, OffsetSpec.Latest());
        var earliest = await ListOffsetsAsync(admin, partitions, OffsetSpec.Earliest());

        var lag = new Dictionary<int, long>();
        foreach (var (partition, high) in latest)
        {
            // No committed offset yet: the consumer starts from the earliest retained offset.
            var position = committed.TryGetValue(partition, out var offset) && offset >= 0
                ? offset
                : earliest.GetValueOrDefault(partition, high);

            lag[partition] = Math.Max(0, high - position);
        }

        OrderFlowMetrics.SetConsumerLag(_kafka.Topic, lag);
    }

    private static async Task<Dictionary<int, long>> ListOffsetsAsync(
        IAdminClient admin,
        IEnumerable<TopicPartition> partitions,
        OffsetSpec spec)
    {
        var result = await admin.ListOffsetsAsync(
            partitions.Select(tp => new TopicPartitionOffsetSpec { TopicPartition = tp, OffsetSpec = spec }),
            new ListOffsetsOptions { RequestTimeout = RequestTimeout });

        return result.ResultInfos.ToDictionary(
            info => info.TopicPartitionOffsetError.Partition.Value,
            info => info.TopicPartitionOffsetError.Offset.Value);
    }
}
