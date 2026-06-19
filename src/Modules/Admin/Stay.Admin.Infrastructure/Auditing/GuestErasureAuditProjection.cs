using System.Text.Json;
using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Guest.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>
/// Projects data-subject erasures into <c>admin.audit_log</c> (CLAUDE.md §10 — "PII access/export/
/// erasure and any data-subject request action"). PII is minimized: the event carries only counts, so
/// the audit row evidences that the erasure happened without re-storing the erased personal data.
/// </summary>
public sealed class GuestErasureAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string ErasedType = "stay.guest.erased";

    public override bool Handles(string eventType) => eventType == ErasedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) =>
        envelope.Type == ErasedType ? ToAudit(AuditPayload.Deserialize<GuestErased>(envelope.Payload)) : null;

    private static AuditLogEntry ToAudit(GuestErased e) => AuditLogEntry.Record(
        e.ActorSub, "guest.erase", "guest", e.GuestId.ToString(),
        before: null,
        after: JsonSerializer.Serialize(new { travelers_deleted = e.TravelersDeleted, payment_tokens_deleted = e.PaymentTokensDeleted }),
        reason: "data-subject erasure (BR-8)");
}
