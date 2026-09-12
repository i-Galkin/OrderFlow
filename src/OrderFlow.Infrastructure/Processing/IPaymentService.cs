namespace OrderFlow.Infrastructure.Processing;

public enum PaymentOutcome
{
    Success = 0,
    TransientFailure = 1,
    PermanentFailure = 2
}

public sealed record PaymentResult(PaymentOutcome Outcome, string? Reason = null, string? AuthorizationCode = null)
{
    public bool IsSuccess => Outcome == PaymentOutcome.Success;

    public static PaymentResult Success(string authorizationCode) =>
        new(PaymentOutcome.Success, null, authorizationCode);

    public static PaymentResult Transient(string reason) => new(PaymentOutcome.TransientFailure, reason);

    public static PaymentResult Permanent(string reason) => new(PaymentOutcome.PermanentFailure, reason);
}

public interface IPaymentService
{
    Task<PaymentResult> ChargeAsync(Guid orderId, Guid customerId, decimal amount, CancellationToken ct = default);
}
