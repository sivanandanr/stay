using System.Text.Json;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;

namespace Stay.Search.Infrastructure;

/// <summary>
/// Keeps the search amenities facet in sync: when a property's amenity set changes, the full code list
/// rides on <see cref="PropertyAmenitiesUpdated"/> (event-carried state) and overwrites the index doc's
/// <c>amenities</c>. Idempotent (full replace); eventually consistent with the catalog.
/// </summary>
public sealed class SearchAmenitiesProjection(IPropertySearchIndex index)
{
    public static bool Handles(string eventType) => eventType == "stay.catalog.property-amenities-updated";

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var e = JsonSerializer.Deserialize<PropertyAmenitiesUpdated>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty PropertyAmenitiesUpdated payload.");

        if (e.PropertyId <= 0)
            return;

        await index.UpdateAmenitiesAsync(e.PropertyId, e.Amenities ?? [], ct);
    }
}
