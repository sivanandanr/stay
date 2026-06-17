using Stay.BuildingBlocks.Messaging;

namespace Stay.Payment.Contracts;

/// <summary>Body for opening a dispute (typically from a PSP <c>payment.disputed</c> webhook).</summary>
public sealed record OpenDisputeRequest(long PaymentId, string PspDisputeId, decimal Amount, string Currency, string? Reason);

/// <summary>A recorded chargeback / dispute.</summary>
public sealed record DisputeResponse(long Id, long PaymentId, string Status, decimal Amount, string Currency);

/// <summary>Body for <c>POST /api/v1/admin/disputes/{id}/resolve</c> — outcome + mandatory note (§10).</summary>
public sealed record ResolveDisputeRequest(string Outcome, string Resolution);

/// <summary>
/// Emitted when finance resolves a dispute (WON / LOST / ACCEPTED) — audit evidence (§10). The Admin
/// context records it in <c>admin.audit_log</c>.
/// </summary>
public sealed record DisputeResolved(
    Guid EventId, long DisputeId, long PaymentId, string Outcome, string ActorSub, string Resolution, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.payment.dispute-resolved";
}
