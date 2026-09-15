using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Infrastructure.Observability;
using OrderFlow.Infrastructure.Processing;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class OrderFlowMetricsTests
{
    [Theory]
    [InlineData(100.13, "PermanentFailure", "card_declined")]
    [InlineData(100.77, "TransientFailure", "gateway_timeout")]
    [InlineData(12_000.00, "TransientFailure", "manual_review_required")]
    [InlineData(10.00, "Success", "none")]
    public async Task Payments_are_counted_by_outcome_and_reason(decimal amount, string outcome, string reason)
    {
        using var collector = new MeasurementCollector("orderflow.payments");
        var payments = new FakePaymentService(NullLogger<FakePaymentService>.Instance);

        await payments.ChargeAsync(Guid.NewGuid(), Guid.NewGuid(), amount);

        // Other tests run the payment stub in parallel, so look for our measurement rather than
        // asserting on a total.
        collector.Measurements.Should().Contain(m =>
            m.Value == 1 && Equals(m.Tags["outcome"], outcome) && Equals(m.Tags["reason"], reason));
    }

    [Fact]
    public void Health_check_status_is_exposed_as_a_gauge()
    {
        var check = $"test-{Guid.NewGuid():N}";
        using var collector = new MeasurementCollector("orderflow.health.check.status");

        OrderFlowMetrics.SetHealthCheckStatus(check, HealthStatus.Degraded);
        collector.RecordObservableInstruments();

        collector.Measurements.Should().Contain(m => Equals(m.Tags["check"], check) && m.Value == 0.5);
    }

    [Fact]
    public void Consumer_lag_snapshot_replaces_partitions_that_disappeared()
    {
        var topic = $"test-{Guid.NewGuid():N}";
        using var collector = new MeasurementCollector("orderflow.kafka.consumer.lag");

        OrderFlowMetrics.SetConsumerLag(topic, new Dictionary<int, long> { [0] = 5, [1] = 7 });
        OrderFlowMetrics.SetConsumerLag(topic, new Dictionary<int, long> { [1] = 3 });
        collector.RecordObservableInstruments();

        collector.Measurements
            .Where(m => Equals(m.Tags["topic"], topic))
            .Should().ContainSingle()
            .Which.Should().Match<Measurement>(m => Equals(m.Tags["partition"], 1) && m.Value == 3);
    }

    [Fact]
    public void Unknown_event_types_are_not_used_as_label_values()
    {
        using var collector = new MeasurementCollector("orderflow.worker.messages");

        OrderFlowMetrics.RecordMessageHandled($"Surprise-{Guid.NewGuid():N}", "test_outcome", TimeSpan.Zero);

        collector.Measurements.Should().Contain(m =>
            Equals(m.Tags["outcome"], "test_outcome") && Equals(m.Tags["event_type"], "other"));
    }

    public sealed record Measurement(double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class MeasurementCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MeasurementCollector(string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OrderFlowMetrics.MeterName && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Add(value, tags));
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) => Add(value, tags));
            _listener.Start();
        }

        public ConcurrentQueue<Measurement> Measurements { get; } = new();

        public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();

        private void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            Measurements.Enqueue(new Measurement(value, copy));
        }
    }
}
