using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects property moderation decisions (publish/reject) into <c>admin.audit_log</c> (CLAUDE.md §10).</summary>
public sealed class PropertyModerationAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string PublishedType = "stay.catalog.property-published";
    public const string RejectedType = "stay.catalog.property-rejected";

    public override bool Handles(string eventType) => eventType is PublishedType or RejectedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) => envelope.Type switch
    {
        PublishedType => ToAudit(AuditPayload.Deserialize<PropertyPublished>(envelope.Payload)),
        RejectedType => ToAudit(AuditPayload.Deserialize<PropertyRejected>(envelope.Payload)),
        _ => null
    };

    private static AuditLogEntry ToAudit(PropertyPublished e) => AuditLogEntry.Record(
        e.ActorSub, "property.publish", "property", e.PropertyId.ToString(),
        before: AuditPayload.Status("IN_REVIEW"), after: AuditPayload.Status("LIVE"), reason: null);

    private static AuditLogEntry ToAudit(PropertyRejected e) => AuditLogEntry.Record(
        e.ActorSub, "property.reject", "property", e.PropertyId.ToString(),
        before: AuditPayload.Status("IN_REVIEW"), after: AuditPayload.Status("DRAFT"), reason: e.Reason);
}
