using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure;

/// <summary>
/// Local fake so Stay runs without the real PaymentGateway service (CLAUDE.md §3). Authorizes and
/// captures successfully, returning a deterministic-per-key reference. Real RazorPay integration
/// lives behind the same port in the shared PaymentGateway service.
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction instruction, CancellationToken ct = default) =>
        Task.FromResult(AuthorizationResult.Approved($"fake_auth_{instruction.IdempotencyKey}"));

    public Task<CaptureResult> CaptureAsync(string pspRef, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(CaptureResult.Ok());

    public Task<RefundResult> RefundAsync(string capturedPspRef, decimal amount, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(RefundResult.Ok($"fake_refund_{idempotencyKey}"));

    // The common missed-webhook case: the capture succeeded at the PSP, the webhook just never arrived.
    public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
        Task.FromResult(GatewayPaymentStatus.Captured);
}
