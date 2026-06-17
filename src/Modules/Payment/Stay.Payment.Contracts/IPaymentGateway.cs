namespace Stay.Payment.Contracts;

/// <summary>A request to authorize a payment for a booking. Tagged <c>source=stay</c> by the gateway wrapper (§9).</summary>
public sealed record PaymentInstruction(string IdempotencyKey, long BookingId, decimal Amount, string Currency);

/// <summary>Result of an authorization attempt.</summary>
public sealed record AuthorizationResult(bool Authorized, string? PspRef, string? DeclineReason)
{
    public static AuthorizationResult Approved(string pspRef) => new(true, pspRef, null);
    public static AuthorizationResult Declined(string reason) => new(false, null, reason);
}

/// <summary>Result of a capture attempt.</summary>
public sealed record CaptureResult(bool Captured, string? FailureReason)
{
    public static CaptureResult Ok() => new(true, null);
    public static CaptureResult Failed(string reason) => new(false, reason);
}

/// <summary>Result of a refund attempt.</summary>
public sealed record RefundResult(bool Refunded, string? PspRef, string? FailureReason)
{
    public static RefundResult Ok(string pspRef) => new(true, pspRef, null);
    public static RefundResult Failed(string reason) => new(false, null, reason);
}

/// <summary>The authoritative PSP state of a payment, as reported by the gateway (for reconciliation).</summary>
public enum GatewayPaymentStatus
{
    Pending,
    Authorized,
    Captured,
    Failed,
    Unknown
}

/// <summary>
/// Port over the existing PaymentGateway service (the RazorPay wrapper). Stay calls payment ops
/// ONLY through this — never the RazorPay SDK directly (CLAUDE.md §9). Every call carries an
/// idempotency key (<c>stay:{booking_id}:{attempt}</c>) so replays return the original result (BR-5).
/// </summary>
public interface IPaymentGateway
{
    Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction instruction, CancellationToken ct = default);
    Task<CaptureResult> CaptureAsync(string pspRef, string idempotencyKey, CancellationToken ct = default);
    Task<RefundResult> RefundAsync(string capturedPspRef, decimal amount, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Queries the authoritative PSP state of a payment — the poll-reconciler's backstop for missed webhooks (§9).</summary>
    Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default);
}
