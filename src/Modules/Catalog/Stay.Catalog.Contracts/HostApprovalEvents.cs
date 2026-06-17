using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>
/// Raised when an admin approves a host (→ ACTIVE). Written to the catalog outbox in the same
/// transaction as the status change — the durable, append-only evidence of the privileged action
/// (CLAUDE.md §10); an admin.audit_log projection consumes it.
/// </summary>
public sealed record HostApproved(
    Guid EventId, long HostId, string ActorSub, string PreviousStatus, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.host-approved";
}

/// <summary>Raised when an admin rejects a host (→ SUSPENDED), carrying the mandatory reason.</summary>
public sealed record HostRejected(
    Guid EventId, long HostId, string ActorSub, string PreviousStatus, string Reason, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.host-rejected";
}
