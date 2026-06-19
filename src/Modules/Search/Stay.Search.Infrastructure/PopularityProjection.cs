using System.Text.Json;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Search.Infrastructure;

/// <summary>
/// Feeds the trending signal: each confirmed booking bumps its property's popularity in the search
/// index (event-carried state — the property id rides on <see cref="BookingConfirmed"/>, so no read-back
/// into Booking, BR-6). Eventually consistent; an at-least-once redelivery slightly over-counts, which
/// is acceptable for a ranking signal (not money).
/// </summary>
public sealed class PopularityProjection(IPropertySearchIndex index)
{
    public static bool Handles(string eventType) => eventType == "stay.booking.confirmed";

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var e = JsonSerializer.Deserialize<BookingConfirmed>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty BookingConfirmed payload.");

        if (e.PropertyId <= 0)
            return; // an older event emitted before the property id was carried

        await index.IncrementPopularityAsync(e.PropertyId, ct);
    }
}
