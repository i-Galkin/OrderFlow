using FluentAssertions;
using OrderFlow.Contracts;
using OrderFlow.Infrastructure.Domain;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class OrderStatusTransitionTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Processing)]
    [InlineData(OrderStatus.Processing, OrderStatus.Completed)]
    [InlineData(OrderStatus.Processing, OrderStatus.Failed)]
    [InlineData(OrderStatus.Failed, OrderStatus.Processing)]
    public void Allowed_transitions_are_accepted(OrderStatus from, OrderStatus to)
    {
        OrderStatusTransitions.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Completed)]
    [InlineData(OrderStatus.Pending, OrderStatus.Processing)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Completed)]
    [InlineData(OrderStatus.Completed, OrderStatus.Processing)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Failed, OrderStatus.Completed)]
    public void Nonsensical_transitions_are_rejected(OrderStatus from, OrderStatus to)
    {
        OrderStatusTransitions.CanTransition(from, to).Should().BeFalse();
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Cancelled)]
    public void Terminal_states_have_no_successors(OrderStatus status)
    {
        OrderStatusTransitions.IsTerminal(status).Should().BeTrue();
        OrderStatusTransitions.AllowedFrom(status).Should().BeEmpty();
    }
}
