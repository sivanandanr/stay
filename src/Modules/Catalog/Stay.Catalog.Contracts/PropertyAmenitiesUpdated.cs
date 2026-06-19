using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Contracts;

/// <summary>
/// Raised when a property's amenity set is replaced. Carries the full list of amenity <c>codes</c>
/// (event-carried state) so the search read model can overwrite its <c>amenities</c> field without
/// reading back into the catalog context.
/// </summary>
public sealed record PropertyAmenitiesUpdated(
    Guid EventId,
    long PropertyId,
    IReadOnlyList<string> Amenities,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "stay.catalog.property-amenities-updated";
}
