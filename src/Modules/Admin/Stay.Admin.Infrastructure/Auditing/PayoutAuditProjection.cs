using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Payment.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects payout runs (PAID/FAILED) into <c>admin.audit_log</c> (CLAUDE.md §10 — "payout runs").</summary>
public sealed class PayoutAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string CompletedType = "stay.payment.payout-completed";

    public override bool Handles(string eventType) => eventType == CompletedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) =>
        envelope.Type == CompletedType ? ToAudit(AuditPayload.Deserialize<PayoutCompleted>(envelope.Payload)) : null;

    private static AuditLogEntry ToAudit(PayoutCompleted e) => AuditLogEntry.Record(
        e.ActorSub, "payout.run", "payout", e.PayoutId.ToString(),
        before: AuditPayload.Status("DRAFT"),
        after: AuditPayload.Status(e.Status),
        reason: $"net {e.NetAmount:0.00} {e.Currency} to host {e.HostId}");
}
