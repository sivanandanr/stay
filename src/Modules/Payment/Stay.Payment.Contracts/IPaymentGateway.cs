namespace Stay.Payment.Contracts;

/// <summary>A request to authorize a payment for a booking. Tagged <c>source=stay</c> by the gateway wrapper (§9).</summary>
public sealed record PaymentInstruction(string IdempotencyKey, long BookingId, decimal Amount, string Currency);

/// <summary>
/// A request to open a Razorpay Checkout order for a held booking — the client-driven payment path (§9):
/// Stay asks PaymentGateway for an order at confirm time, the mobile client completes payment
/// (cards/UPI/netbanking; 3DS handled by Razorpay checkout). SCAFFOLD — unverified against the real
/// PaymentGateway order contract.
/// </summary>
public sealed record OrderRequest(string IdempotencyKey, long BookingId, decimal Amount, string Currency);

/// <summary>
/// Checkout parameters the mobile client feeds to the Razorpay Checkout SDK. <c>KeyId</c> is the public
/// checkout key (never a secret — PCI SAQ-A, §9/§12); <c>PspOrderId</c> is the Razorpay order id the
/// client opens checkout against. SCAFFOLD — shape may change once the real PaymentGateway is wired.
/// </summary>
public sealed record OrderResult(string PspOrderId, string KeyId, decimal Amount, string Currency);

/// <summary>
/// The Razorpay Checkout result the mobile client returns after paying: the order it paid against, the
/// resulting payment id, and the checkout <c>signature</c>. Stay forwards this to PaymentGateway for
/// verification — Stay never computes the HMAC itself (the secret lives in PaymentGateway, §9/§12).
/// </summary>
public sealed record CheckoutProof(string OrderId, string PaymentId, string Signature);

/// <summary>Result of verifying a <see cref="CheckoutProof"/>. On success <c>PspRef</c> is the verified payment id.</summary>
public sealed record VerificationResult(bool Verified, string? PspRef, string? Reason)
{
    public static VerificationResult Ok(string pspRef) => new(true, pspRef, null);
    public static VerificationResult Failed(string reason) => new(false, null, reason);
}

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

/// <summary>The authoritative PSP state of a refund (for daily ledger reconciliation, Gate G2).</summary>
public enum GatewayRefundStatus
{
    Pending,
    Processed,
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
    /// <summary>
    /// Creates a Razorpay Checkout order for client-side payment (cards/UPI/netbanking; 3DS handled by
    /// Razorpay). The saga suspends here and resumes on the client's confirm callback within the hold TTL
    /// (§9). SCAFFOLD — unverified against the real PaymentGateway contract.
    /// </summary>
    Task<OrderResult> CreateOrderAsync(OrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verifies a completed Razorpay Checkout payment (the client-driven path): PaymentGateway holds the
    /// secret and performs the HMAC signature check over <c>order_id|payment_id</c>, returning the verified
    /// payment id. Idempotent by the idempotency key (replays return the original verdict, BR-5). Stay must
    /// never verify the signature itself (§9).
    /// </summary>
    Task<VerificationResult> VerifyCheckoutAsync(CheckoutProof proof, string idempotencyKey, CancellationToken ct = default);

    Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction instruction, CancellationToken ct = default);
    Task<CaptureResult> CaptureAsync(string pspRef, string idempotencyKey, CancellationToken ct = default);
    Task<RefundResult> RefundAsync(string capturedPspRef, decimal amount, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Queries the authoritative PSP state of a payment — the poll-reconciler's backstop for missed webhooks (§9).</summary>
    Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default);

    /// <summary>Queries the authoritative PSP state of a refund — used by daily ledger reconciliation (Gate G2).</summary>
    Task<GatewayRefundStatus> GetRefundStatusAsync(string refundPspRef, CancellationToken ct = default);
}
