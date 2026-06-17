using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects ops manual booking overrides into <c>admin.audit_log</c> (CLAUDE.md §10).</summary>
public sealed class BookingOverrideAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string OverriddenType = "stay.booking.overridden";

    public override bool Handles(string eventType) => eventType == OverriddenType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) =>
        envelope.Type == OverriddenType ? ToAudit(AuditPayload.Deserialize<BookingOverridden>(envelope.Payload)) : null;

    private static AuditLogEntry ToAudit(BookingOverridden e) => AuditLogEntry.Record(
        e.ActorSub, "booking.override", "booking", e.BookingId.ToString(),
        before: AuditPayload.Status(e.FromStatus), after: AuditPayload.Status(e.ToStatus), reason: e.Reason);
}
