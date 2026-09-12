using FluentAssertions;
using OrderFlow.Contracts;
using OrderFlow.Infrastructure.Domain;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class OrderDomainTests
{
    private static Order NewOrder(params (decimal price, int qty)[] lines)
    {
        var items = lines.Select(line => new OrderItem
        {
            ProductId = Guid.NewGuid(),
            Quantity = line.qty,
            UnitPrice = line.price
        });

        return Order.Create(Guid.NewGuid(), items);
    }

    [Fact]
    public void Create_sums_the_line_totals()
    {
        var order = NewOrder((10.50m, 2), (4.25m, 4));

        order.TotalAmount.Should().Be(38.00m);
        order.Status.Should().Be(OrderStatus.Pending);
        order.Items.Should().OnlyContain(i => i.OrderId == order.Id);
    }

    [Fact]
    public void Create_rejects_an_empty_basket()
    {
        var act = () => Order.Create(Guid.NewGuid(), Array.Empty<OrderItem>());

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Confirm_moves_a_pending_order_forward_and_bumps_the_version()
    {
        var order = NewOrder((10m, 1));
        var version = order.Version;

        order.Confirm();

        order.Status.Should().Be(OrderStatus.Confirmed);
        order.Version.Should().Be(version + 1);
    }

    [Fact]
    public void Cancelling_a_completed_order_is_rejected()
    {
        var order = NewOrder((10m, 1));
        order.Confirm();
        order.StartProcessing();
        order.Complete();

        var act = () => order.Cancel();

        act.Should().Throw<InvalidStatusTransitionException>()
            .Which.From.Should().Be(nameof(OrderStatus.Completed));
    }

    [Fact]
    public void A_failed_order_can_be_pushed_back_into_processing()
    {
        var order = NewOrder((10m, 1));
        order.Confirm();
        order.StartProcessing();
        order.Fail("card_declined");

        order.FailureReason.Should().Be("card_declined");

        order.StartProcessing();
        order.Complete();

        order.Status.Should().Be(OrderStatus.Completed);
        order.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Completing_an_order_that_never_started_processing_is_rejected()
    {
        var order = NewOrder((10m, 1));
        order.Confirm();

        var act = () => order.Complete();

        act.Should().Throw<InvalidStatusTransitionException>();
    }
}
