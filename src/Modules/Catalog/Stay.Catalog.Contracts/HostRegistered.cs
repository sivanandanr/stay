using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>
/// Raised once when a host self-registers (PENDING approval). Written to the catalog outbox in the
/// same transaction as the insert; consumers may notify the admin approval queue.
/// </summary>
public sealed record HostRegistered(Guid EventId, long HostId, string IdentitySub, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.host-registered";
}
