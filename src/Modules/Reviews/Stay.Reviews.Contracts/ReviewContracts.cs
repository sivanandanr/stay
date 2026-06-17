using Stay.BuildingBlocks.Messaging;

namespace Stay.Reviews.Contracts;

/// <summary>Body for <c>POST /api/v1/reviews</c> — review a completed stay (the guest comes from the token).</summary>
public sealed record SubmitReviewRequest(long BookingId, short OverallRating, string? Title, string? Body);

/// <summary>A review of a property.</summary>
public sealed record ReviewResponse(
    long Id, long PropertyId, long GuestId, short OverallRating, string? Title, string? Body,
    string Status, DateTime CreatedAt); // UTC; Dapper reads timestamptz as DateTime

/// <summary>Body for <c>POST /api/v1/admin/reviews/{id}/reject</c> — a moderation reason is required.</summary>
public sealed record RejectReviewRequest(string Reason);

/// <summary>A property's published-review rating aggregate.</summary>
public sealed record RatingAggregateResponse(long PropertyId, int ReviewCount, decimal AvgOverall);

/// <summary>
/// A moderator published a submitted review (PENDING → PUBLISHED). Carries the actor for the
/// audit trail (§10); the Admin context projects it into <c>admin.audit_log</c>.
/// </summary>
public sealed record ReviewPublished(
    Guid EventId, long ReviewId, long PropertyId, string ActorSub, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.reviews.review-published";
}

/// <summary>A moderator rejected a submitted review (PENDING → REJECTED) with a mandatory reason (§10).</summary>
public sealed record ReviewRejected(
    Guid EventId, long ReviewId, long PropertyId, string ActorSub, string Reason, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.reviews.review-rejected";
}
