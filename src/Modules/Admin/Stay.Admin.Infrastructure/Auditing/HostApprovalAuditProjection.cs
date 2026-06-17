using System.Text.Json;
using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects host approval/rejection events into <c>admin.audit_log</c> (CLAUDE.md §10).</summary>
public sealed class HostApprovalAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string ApprovedType = "stay.catalog.host-approved";
    public const string RejectedType = "stay.catalog.host-rejected";

    public override bool Handles(string eventType) => eventType is ApprovedType or RejectedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) => envelope.Type switch
    {
        ApprovedType => ToAudit(AuditPayload.Deserialize<HostApproved>(envelope.Payload)),
        RejectedType => ToAudit(AuditPayload.Deserialize<HostRejected>(envelope.Payload)),
        _ => null
    };

    private static AuditLogEntry ToAudit(HostApproved e) => AuditLogEntry.Record(
        e.ActorSub, "host.approve", "host", e.HostId.ToString(),
        before: AuditPayload.Status(e.PreviousStatus), after: AuditPayload.Status("ACTIVE"), reason: null);

    private static AuditLogEntry ToAudit(HostRejected e) => AuditLogEntry.Record(
        e.ActorSub, "host.reject", "host", e.HostId.ToString(),
        before: AuditPayload.Status(e.PreviousStatus), after: AuditPayload.Status("SUSPENDED"), reason: e.Reason);
}

/// <summary>Small JSON helpers shared by the audit projections.</summary>
internal static class AuditPayload
{
    public static string Status(string status) => JsonSerializer.Serialize(new { status });

    public static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidOperationException($"Empty {typeof(T).Name} payload.");
}
