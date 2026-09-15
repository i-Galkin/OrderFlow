using Microsoft.Extensions.Logging;
using OrderFlow.Infrastructure.Observability;

namespace OrderFlow.Infrastructure.Processing;

/// <summary>
/// Stand-in for the payment gateway. The outcome is derived from the order amount so that
/// the same order always produces the same result and integration tests stay repeatable.
///
/// Rules (documented for QA):
///   * amount ending in .13 -> permanently declined ("card_declined")
///   * amount ending in .77 -> transient gateway timeout
///   * amount above 10,000  -> transient manual review
///   * anything else        -> authorised
/// </summary>
public sealed class FakePaymentService : IPaymentService
{
    private readonly ILogger<FakePaymentService> _logger;

    public FakePaymentService(ILogger<FakePaymentService> logger)
    {
        _logger = logger;
    }

    public Task<PaymentResult> ChargeAsync(Guid orderId, Guid customerId, decimal amount, CancellationToken ct = default)
    {
        var cents = (int)(Math.Round(amount, 2) * 100) % 100;

        PaymentResult result;
        if (cents == 13)
        {
            result = PaymentResult.Permanent("card_declined");
        }
        else if (cents == 77)
        {
            result = PaymentResult.Transient("gateway_timeout");
        }
        else if (amount > 10_000m)
        {
            result = PaymentResult.Transient("manual_review_required");
        }
        else
        {
            result = PaymentResult.Success($"AUTH-{orderId.ToString("N")[..8].ToUpperInvariant()}");
        }

        OrderFlowMetrics.RecordPayment(result.Outcome.ToString(), result.Reason);

        _logger.LogDebug(
            "Payment for order {OrderId} of {Amount} resolved as {Outcome}",
            orderId, amount, result.Outcome);

        return Task.FromResult(result);
    }
}
