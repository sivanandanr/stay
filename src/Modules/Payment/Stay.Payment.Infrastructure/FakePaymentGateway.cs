using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure;

/// <summary>
/// Local fake so Stay runs without the real PaymentGateway service (CLAUDE.md §3). Authorizes and
/// captures successfully, returning a deterministic-per-key reference. Real RazorPay integration
/// lives behind the same port in the shared PaymentGateway service.
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    // SCAFFOLD: a real PaymentGateway returns a Razorpay order id + the public checkout key. The fake
    // returns a deterministic-per-key order so the mobile funnel's order→checkout→confirm wiring can be
    // exercised end-to-end without a live PSP. KeyId is a placeholder public key, never a secret.
    public Task<OrderResult> CreateOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(new OrderResult(
            PspOrderId: $"order_fake_{request.IdempotencyKey}",
            KeyId: "rzp_test_stay_scaffold",
            Amount: request.Amount,
            Currency: request.Currency));

    // SCAFFOLD: the real PaymentGateway recomputes the HMAC over order_id|payment_id with the Razorpay
    // secret. The fake accepts any non-blank signature and rejects a blank/"invalid" one so the
    // verify-failure path is exercisable. The verified PSP ref is the payment id.
    public Task<VerificationResult> VerifyCheckoutAsync(CheckoutProof proof, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(proof.Signature) || proof.Signature == "invalid"
            ? VerificationResult.Failed("signature_mismatch")
            : VerificationResult.Ok(proof.PaymentId));

    public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction instruction, CancellationToken ct = default) =>
        Task.FromResult(AuthorizationResult.Approved($"fake_auth_{instruction.IdempotencyKey}"));

    public Task<CaptureResult> CaptureAsync(string pspRef, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(CaptureResult.Ok());

    public Task<RefundResult> RefundAsync(string capturedPspRef, decimal amount, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(RefundResult.Ok($"fake_refund_{idempotencyKey}"));

    // The common missed-webhook case: the capture succeeded at the PSP, the webhook just never arrived.
    public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
        Task.FromResult(GatewayPaymentStatus.Captured);

    public Task<GatewayRefundStatus> GetRefundStatusAsync(string refundPspRef, CancellationToken ct = default) =>
        Task.FromResult(GatewayRefundStatus.Processed);
}
