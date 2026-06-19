using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;

namespace Stay.Ari.Infrastructure.Availability;

/// <summary>
/// A read-only "rooms &amp; rates" preview for the booking funnel: the deterministic quote (BR-2 price
/// pipeline) plus how many units are bookable across the stay. NON-authoritative — the atomic hold
/// (BR-1) remains the only truth; a price/availability shown here can change before the guest holds,
/// which the funnel handles via the sold-out / price-changed recovery flows.
/// </summary>
public sealed record RoomQuote(
    long RoomTypeId,
    long RatePlanId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Quantity,
    bool Available,
    int AvailableUnits,
    string? Currency,
    decimal? PerRoomTotal,
    decimal? StayTotal,
    IReadOnlyList<NightPrice> Nights);

/// <summary>Prices a room type + rate plan for a stay and reports availability, without holding inventory.</summary>
public sealed class AvailabilityService(string connectionString)
{
    private readonly RateRepository _rates = new();
    private readonly InventoryRepository _inventory = new();

    public async Task<RoomQuote> QuoteAsync(
        long roomTypeId, long ratePlanId, DateOnly checkIn, DateOnly checkOut, int occupancy, int quantity,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var quote = await _rates.QuoteAsync(conn, roomTypeId, ratePlanId, checkIn, checkOut, occupancy, tx: null, ct);
        var availableUnits = await _inventory.AvailableUnitsAsync(conn, roomTypeId, checkIn, checkOut, tx: null, ct);

        var available = quote is not null && availableUnits >= quantity;

        return new RoomQuote(
            roomTypeId, ratePlanId, checkIn, checkOut, quantity,
            Available: available,
            AvailableUnits: availableUnits,
            Currency: quote?.Currency,
            PerRoomTotal: quote?.Total,
            StayTotal: quote is null ? null : quote.Total * quantity,
            Nights: quote?.Nights ?? []);
    }
}
