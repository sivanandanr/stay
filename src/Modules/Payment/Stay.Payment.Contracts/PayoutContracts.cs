using Stay.BuildingBlocks.Messaging;

namespace Stay.Payment.Contracts;

// ── Payout gateway port (Razorpay Route) ──────────────────────────────────────

/// <summary>A request to pay a host their net earnings to a Razorpay Route linked account (§9).</summary>
public sealed record PayoutInstruction(string IdempotencyKey, string LinkedAccountRef, decimal NetAmount, string Currency);

/// <summary>Result of a payout attempt.</summary>
public sealed record PayoutResult(bool Paid, string? PspRef, string? FailureReason)
{
    public static PayoutResult Ok(string pspRef) => new(true, pspRef, null);
    public static PayoutResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Port over Razorpay Route payouts — separate from the booking-payment <see cref="IPaymentGateway"/>
/// (Orders/Checkout) and from food settlements (§9). Stay never calls the Razorpay SDK directly; every
/// payout carries an idempotency key (<c>stay:payout:{payout_id}</c>) so replays don't double-pay (BR-5).
/// </summary>
public interface IPayoutGateway
{
    Task<PayoutResult> PayoutAsync(PayoutInstruction instruction, CancellationToken ct = default);

    /// <summary>Queries the authoritative PSP state of a payout — used by daily ledger reconciliation (Gate G2).</summary>
    Task<GatewayPayoutStatus> GetPayoutStatusAsync(string payoutPspRef, CancellationToken ct = default);
}

/// <summary>The authoritative PSP state of a payout (for daily ledger reconciliation, Gate G2).</summary>
public enum GatewayPayoutStatus
{
    Pending,
    Paid,
    Failed,
    Unknown
}

// ── Payout generation + execution DTOs ────────────────────────────────────────

/// <summary>One eligible booking's gross earning for a host in a statement period (fed by the earnings ledger).</summary>
public sealed record PayoutLineInput(long BookingId, decimal Gross);

/// <summary>Body for <c>POST /api/v1/admin/payouts</c> — generate a host's statement for a period.</summary>
public sealed record GeneratePayoutRequest(
    long HostId, DateOnly PeriodStart, DateOnly PeriodEnd, decimal CommissionPct, string Currency,
    IReadOnlyList<PayoutLineInput> Lines);

/// <summary>A generated payout statement (DRAFT until executed).</summary>
public sealed record PayoutResponse(
    long Id, long HostId, decimal GrossAmount, decimal Commission, decimal NetAmount, string Currency, string Status);

/// <summary>Outcome of executing a payout against the gateway.</summary>
public sealed record PayoutExecutionResponse(long Id, string Status, string? StatementRef, string? FailureReason);

/// <summary>
/// Emitted when a payout run completes (PAID or FAILED) — audit evidence for §10 ("payout runs"); the
/// Admin context records it in <c>admin.audit_log</c>.
/// </summary>
public sealed record PayoutCompleted(
    Guid EventId, long PayoutId, long HostId, decimal NetAmount, string Currency, string Status,
    string ActorSub, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "stay.payment.payout-completed";
}
