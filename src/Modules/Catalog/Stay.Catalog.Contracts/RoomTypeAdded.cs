using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>
/// Raised when a room type is added to a property. Written to the catalog outbox in the same
/// transaction as the insert; ARI/search consumers use it to seed inventory/indexing.
/// </summary>
public sealed record RoomTypeAdded(Guid EventId, long RoomTypeId, long PropertyId, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.catalog.room-type-added";
}
