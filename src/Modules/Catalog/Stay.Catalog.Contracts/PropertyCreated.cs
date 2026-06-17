using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>
/// Raised when a property is created (in DRAFT). Written to the catalog outbox in the same
/// transaction as the insert. Carries the denormalized fields the search read model needs, so the
/// indexer never reads back into the catalog context (event-carried state).
/// </summary>
public sealed record PropertyCreated(
    Guid EventId,
    long PropertyId,
    long HostId,
    string Name,
    string PropertyType,
    string CountryCode,
    long CityId,
    string CityName,
    double Latitude,
    double Longitude,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "stay.catalog.property-created";
}
