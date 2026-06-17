using System.Text.Json;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;

namespace Stay.Search.Infrastructure;

/// <summary>
/// Maintains the property search read model from catalog events: a created property is indexed as
/// DRAFT (invisible to search), publishing flips it LIVE (searchable), rejection flips it back. The
/// document is built entirely from event-carried state — no read-back into the catalog context.
/// </summary>
public sealed class SearchIndexProjection(IPropertySearchIndex index)
{
    public const string CreatedType = "stay.catalog.property-created";
    public const string PublishedType = "stay.catalog.property-published";
    public const string RejectedType = "stay.catalog.property-rejected";

    public static bool Handles(string eventType) =>
        eventType is CreatedType or PublishedType or RejectedType;

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        switch (envelope.Type)
        {
            case CreatedType:
                var created = Deserialize<PropertyCreated>(envelope.Payload);
                await index.IndexAsync(new PropertySearchDocument(
                    created.PropertyId, created.Name, created.CityName, created.CountryCode,
                    created.PropertyType, created.Latitude, created.Longitude, FromPrice: null, Status: "DRAFT"), ct);
                break;

            case PublishedType:
                await index.UpdateStatusAsync(Deserialize<PropertyPublished>(envelope.Payload).PropertyId, "LIVE", ct);
                break;

            case RejectedType:
                await index.UpdateStatusAsync(Deserialize<PropertyRejected>(envelope.Payload).PropertyId, "DRAFT", ct);
                break;
        }
    }

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidOperationException($"Empty {typeof(T).Name} payload.");
}
