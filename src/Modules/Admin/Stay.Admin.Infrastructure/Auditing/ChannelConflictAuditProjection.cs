using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Channel.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects channel sync-conflict resolutions into <c>admin.audit_log</c> (CLAUDE.md §10).</summary>
public sealed class ChannelConflictAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string ResolvedType = "stay.channel.conflict-resolved";

    public override bool Handles(string eventType) => eventType == ResolvedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) =>
        envelope.Type == ResolvedType ? ToAudit(AuditPayload.Deserialize<ChannelConflictResolved>(envelope.Payload)) : null;

    private static AuditLogEntry ToAudit(ChannelConflictResolved e) => AuditLogEntry.Record(
        e.ActorSub, "channel.conflict_resolve", "sync_conflict", e.ConflictId.ToString(),
        before: AuditPayload.Status("OPEN"), after: AuditPayload.Status(e.Status), reason: e.Resolution);
}
