using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>
/// A throwaway integration event used to prove the transactional-outbox round-trip (P0-A6):
/// it is written to the catalog outbox in the same transaction as a catalog write, then
/// dispatched to Kafka and observed by a consumer.
/// </summary>
public sealed record TestEvent(Guid EventId, long AmenityId, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "stay.catalog.test-event";
}
