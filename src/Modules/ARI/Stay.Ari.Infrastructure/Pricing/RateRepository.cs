using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;

namespace Stay.Ari.Infrastructure.Pricing;

/// <summary>
/// Rate calendar reads/writes and the deterministic quote pipeline (CLAUDE.md §5 hot path). The
/// quote runs at hold time inside the saga's transaction so the price is the contract (BR-2).
/// </summary>
public sealed class RateRepository
{
    static RateRepository() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    /// <summary>Upserts the base price (and optional occupancy deltas) for every night in <c>[from, toExclusive)</c>.</summary>
    public async Task SetRateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, long ratePlanId, DateOnly from, DateOnly toExclusive,
        decimal basePrice, string currency, IReadOnlyDictionary<int, decimal>? occupancyPrices = null,
        CancellationToken ct = default)
    {
        var json = occupancyPrices is null
            ? null
            : JsonSerializer.Serialize(occupancyPrices.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

        const string sql = """
            INSERT INTO ari.rate_calendar (room_type_id, rate_plan_id, stay_date, base_price, currency, occupancy_prices)
            SELECT @roomTypeId, @ratePlanId, gs::date, @basePrice, @currency, CAST(@json AS jsonb)
            FROM generate_series(@from::date, @toExclusive::date - 1, interval '1 day') AS gs
            ON CONFLICT (room_type_id, rate_plan_id, stay_date) DO UPDATE
                SET base_price       = EXCLUDED.base_price,
                    currency         = EXCLUDED.currency,
                    occupancy_prices = EXCLUDED.occupancy_prices
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, ratePlanId, from, toExclusive, basePrice, currency, json }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Prices a stay over <c>[checkIn, checkOut)</c> for a room type + rate plan at the given occupancy.
    /// Returns null if any night has no rate (not fully priceable) or the nights aren't a single currency.
    /// </summary>
    public async Task<Quote?> QuoteAsync(
        NpgsqlConnection conn, long roomTypeId, long ratePlanId, DateOnly checkIn, DateOnly checkOut, int occupancy,
        NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        var nights = checkOut.DayNumber - checkIn.DayNumber;
        if (nights <= 0)
            return null;

        const string sql = """
            SELECT stay_date AS StayDate, base_price AS BasePrice, currency AS Currency,
                   occupancy_prices AS OccupancyPrices
            FROM ari.rate_calendar
            WHERE room_type_id = @roomTypeId AND rate_plan_id = @ratePlanId
              AND stay_date >= @checkIn AND stay_date < @checkOut
            ORDER BY stay_date
            """;

        var rows = (await conn.QueryAsync<RateRow>(new CommandDefinition(
            sql, new { roomTypeId, ratePlanId, checkIn, checkOut }, tx, cancellationToken: ct))).AsList();

        if (rows.Count != nights)
            return null; // a night is missing a rate — cannot price the stay

        var currencies = rows.Select(r => r.Currency.Trim()).Distinct().ToList();
        if (currencies.Count != 1)
            return null; // mixed currencies across the stay — inconsistent, refuse to quote

        var breakdown = rows
            .Select(r => new NightPrice(r.StayDate, r.BasePrice + OccupancyDelta(r.OccupancyPrices, occupancy)))
            .ToList();

        return new Quote(currencies[0], breakdown.Sum(n => n.Price), breakdown);
    }

    private static decimal OccupancyDelta(string? occupancyPricesJson, int occupancy)
    {
        if (string.IsNullOrWhiteSpace(occupancyPricesJson))
            return 0m;

        var deltas = JsonSerializer.Deserialize<Dictionary<string, decimal>>(occupancyPricesJson);
        return deltas is not null && deltas.TryGetValue(occupancy.ToString(), out var delta) ? delta : 0m;
    }

    private sealed record RateRow(DateOnly StayDate, decimal BasePrice, string Currency, string? OccupancyPrices);
}
