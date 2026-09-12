using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Infrastructure.Processing;
using Xunit;

namespace OrderFlow.Tests.Unit;

public class FakePaymentServiceTests
{
    private readonly FakePaymentService _payments = new(NullLogger<FakePaymentService>.Instance);

    [Theory]
    [InlineData(100.13)]
    [InlineData(4.13)]
    public async Task Amounts_ending_in_13_are_declined_permanently(decimal amount)
    {
        var result = await _payments.ChargeAsync(Guid.NewGuid(), Guid.NewGuid(), amount);

        result.Outcome.Should().Be(PaymentOutcome.PermanentFailure);
        result.Reason.Should().Be("card_declined");
    }

    [Theory]
    [InlineData(100.77)]
    [InlineData(12_000.00)]
    public async Task Gateway_problems_are_reported_as_transient(decimal amount)
    {
        var result = await _payments.ChargeAsync(Guid.NewGuid(), Guid.NewGuid(), amount);

        result.Outcome.Should().Be(PaymentOutcome.TransientFailure);
    }

    [Theory]
    [InlineData(10.00)]
    [InlineData(99.50)]
    [InlineData(9_999.99)]
    public async Task Ordinary_amounts_are_authorised(decimal amount)
    {
        var result = await _payments.ChargeAsync(Guid.NewGuid(), Guid.NewGuid(), amount);

        result.IsSuccess.Should().BeTrue();
        result.AuthorizationCode.Should().StartWith("AUTH-");
    }

    [Fact]
    public async Task The_same_order_always_gets_the_same_answer()
    {
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var first = await _payments.ChargeAsync(orderId, customerId, 250.13m);
        var second = await _payments.ChargeAsync(orderId, customerId, 250.13m);

        second.Should().BeEquivalentTo(first);
    }
}
