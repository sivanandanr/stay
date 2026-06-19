using System.Text.Json;
using Stay.BuildingBlocks.Outbox;
using Stay.Channel.Contracts;

namespace Stay.Search.Infrastructure;

/// <summary>
/// Keeps the search "from" price fresh: when an applied channel ARI message lowers a property's lead-in
/// rate, the candidate price rides on <see cref="PropertyPriceChanged"/> (event-carried state, BR-6) and
/// the index keeps the minimum. Eventually consistent; the authoritative price is always the frozen
/// quote at hold time (BR-2) — this is only a discovery "from" hint.
/// </summary>
public sealed class SearchPriceProjection(IPropertySearchIndex index)
{
    public static bool Handles(string eventType) => eventType == "stay.channel.property-price-changed";

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var e = JsonSerializer.Deserialize<PropertyPriceChanged>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty PropertyPriceChanged payload.");

        if (e.PropertyId <= 0)
            return;

        await index.UpdateFromPriceAsync(e.PropertyId, e.FromPrice, e.Currency, ct);
    }
}
