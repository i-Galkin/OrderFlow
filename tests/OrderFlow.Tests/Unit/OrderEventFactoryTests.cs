using FluentAssertions;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Observability;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class OrderEventFactoryTests
{
    private static Order SampleOrder()
    {
        return Order.Create(Guid.NewGuid(), new[]
        {
            new OrderItem { ProductId = Guid.NewGuid(), Quantity = 2, UnitPrice = 15m },
            new OrderItem { ProductId = Guid.NewGuid(), Quantity = 1, UnitPrice = 5m }
        });
    }

    [Fact]
    public void Events_carry_the_order_snapshot()
    {
        var order = SampleOrder();

        var evt = OrderEventFactory.From(order, OrderEventTypes.OrderConfirmed);

        evt.EventType.Should().Be(OrderEventTypes.OrderConfirmed);
        evt.OrderId.Should().Be(order.Id);
        evt.CustomerId.Should().Be(order.CustomerId);
        evt.TotalAmount.Should().Be(order.TotalAmount);
        evt.Items.Should().HaveCount(2);
        evt.EventId.Should().NotBeEmpty();
    }

    [Fact]
    public void Events_pick_up_the_ambient_correlation_id()
    {
        var order = SampleOrder();

        using (CorrelationContext.BeginScope("abc123"))
        {
            var evt = OrderEventFactory.From(order, OrderEventTypes.OrderCreated);
            evt.CorrelationId.Should().Be("abc123");
        }

        CorrelationContext.CorrelationId.Should().BeNull();
    }

    [Fact]
    public void Retry_events_record_the_attempt_and_reason()
    {
        var order = SampleOrder();

        var evt = OrderEventFactory.From(order, OrderEventTypes.OrderFailed, attempt: 2, reason: "gateway_timeout");

        evt.Attempt.Should().Be(2);
        evt.Reason.Should().Be("gateway_timeout");
    }
}
