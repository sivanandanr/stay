using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Payment.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects dispute resolutions into <c>admin.audit_log</c> (CLAUDE.md §10 — "disputes").</summary>
public sealed class DisputeAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string ResolvedType = "stay.payment.dispute-resolved";

    public override bool Handles(string eventType) => eventType == ResolvedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) =>
        envelope.Type == ResolvedType ? ToAudit(AuditPayload.Deserialize<DisputeResolved>(envelope.Payload)) : null;

    private static AuditLogEntry ToAudit(DisputeResolved e) => AuditLogEntry.Record(
        e.ActorSub, "dispute.resolve", "dispute", e.DisputeId.ToString(),
        before: AuditPayload.Status("OPEN"), after: AuditPayload.Status(e.Outcome), reason: e.Resolution);
}
