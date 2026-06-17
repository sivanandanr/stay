using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>Raised when an owner submits a property for moderation (DRAFT → IN_REVIEW).</summary>
public sealed record PropertySubmittedForReview(Guid EventId, long PropertyId, long HostId, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.property-submitted";
}

/// <summary>
/// Raised when a moderator publishes a property (IN_REVIEW → LIVE). A privileged moderation
/// decision — audited to admin.audit_log (CLAUDE.md §10). Search may index off this.
/// </summary>
public sealed record PropertyPublished(Guid EventId, long PropertyId, string ActorSub, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.property-published";
}

/// <summary>Raised when a moderator rejects a property back to DRAFT, with the mandatory reason (audited).</summary>
public sealed record PropertyRejected(
    Guid EventId, long PropertyId, string ActorSub, string Reason, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.property-rejected";
}
