using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;
using Stay.Reviews.Contracts;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>Projects review moderation decisions (publish/reject) into <c>admin.audit_log</c> (CLAUDE.md §10).</summary>
public sealed class ReviewModerationAuditProjection(AdminDbContext db) : AuditProjection(db)
{
    public const string PublishedType = "stay.reviews.review-published";
    public const string RejectedType = "stay.reviews.review-rejected";

    public override bool Handles(string eventType) => eventType is PublishedType or RejectedType;

    protected override AuditLogEntry? Map(OutboxEnvelope envelope) => envelope.Type switch
    {
        PublishedType => ToAudit(AuditPayload.Deserialize<ReviewPublished>(envelope.Payload)),
        RejectedType => ToAudit(AuditPayload.Deserialize<ReviewRejected>(envelope.Payload)),
        _ => null
    };

    private static AuditLogEntry ToAudit(ReviewPublished e) => AuditLogEntry.Record(
        e.ActorSub, "review.publish", "review", e.ReviewId.ToString(),
        before: AuditPayload.Status("PENDING"), after: AuditPayload.Status("PUBLISHED"), reason: null);

    private static AuditLogEntry ToAudit(ReviewRejected e) => AuditLogEntry.Record(
        e.ActorSub, "review.reject", "review", e.ReviewId.ToString(),
        before: AuditPayload.Status("PENDING"), after: AuditPayload.Status("REJECTED"), reason: e.Reason);
}
