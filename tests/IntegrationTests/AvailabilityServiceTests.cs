using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Availability;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The funnel's read-only rooms-and-rates preview: deterministic quote (BR-2 pipeline) + bookable-units
/// count, without holding inventory. Proves the available/priceable/sold-out/stop-sell distinctions the
/// guest UI branches on.
/// </summary>
public sealed class AvailabilityServiceTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights (within the test partition window)

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();
    private AvailabilityService _availability = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        _availability = new AvailabilityService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAsync(int allotment, decimal? rate, DateOnly? rateTo = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, allotment);
        if (rate is not null)
            await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, CheckIn, rateTo ?? CheckOut, rate.Value, "INR");
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Prices_and_reports_availability_for_a_bookable_stay()
    {
        await SeedAsync(allotment: 5, rate: 100m);

        var quote = await _availability.QuoteAsync(RoomTypeId, RatePlanId, CheckIn, CheckOut, occupancy: 2, quantity: 1);

        Assert.True(quote.Available);
        Assert.Equal(5, quote.AvailableUnits);
        Assert.Equal("INR", quote.Currency);
        Assert.Equal(300m, quote.PerRoomTotal); // 100 × 3 nights
        Assert.Equal(300m, quote.StayTotal);
        Assert.Equal(3, quote.Nights.Count);
    }

    [Fact]
    public async Task Stay_total_scales_with_quantity()
    {
        await SeedAsync(allotment: 5, rate: 100m);

        var quote = await _availability.QuoteAsync(RoomTypeId, RatePlanId, CheckIn, CheckOut, occupancy: 2, quantity: 2);

        Assert.True(quote.Available);
        Assert.Equal(600m, quote.StayTotal); // 300 × 2 rooms
    }

    [Fact]
    public async Task Unavailable_when_quantity_exceeds_units_but_still_priced()
    {
        await SeedAsync(allotment: 2, rate: 100m);

        var quote = await _availability.QuoteAsync(RoomTypeId, RatePlanId, CheckIn, CheckOut, occupancy: 2, quantity: 3);

        Assert.False(quote.Available);
        Assert.Equal(2, quote.AvailableUnits);
        Assert.Equal(300m, quote.PerRoomTotal); // still priceable, just not enough rooms
    }

    [Fact]
    public async Task Not_priceable_when_a_night_has_no_rate()
    {
        // Inventory for all 3 nights, but a rate only for the first 2.
        await SeedAsync(allotment: 5, rate: 100m, rateTo: CheckOut.AddDays(-1));

        var quote = await _availability.QuoteAsync(RoomTypeId, RatePlanId, CheckIn, CheckOut, occupancy: 2, quantity: 1);

        Assert.False(quote.Available);
        Assert.Null(quote.Currency);
        Assert.Null(quote.PerRoomTotal);
        Assert.Empty(quote.Nights);
        Assert.Equal(5, quote.AvailableUnits); // availability is independent of priceability
    }

    [Fact]
    public async Task Stop_sell_night_makes_the_stay_unavailable()
    {
        await SeedAsync(allotment: 5, rate: 100m);
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE ari.inventory_calendar SET stop_sell = true WHERE room_type_id = @r AND stay_date = @d",
            new { r = RoomTypeId, d = CheckIn.AddDays(1) });

        var quote = await _availability.QuoteAsync(RoomTypeId, RatePlanId, CheckIn, CheckOut, occupancy: 2, quantity: 1);

        Assert.False(quote.Available);
        Assert.Equal(0, quote.AvailableUnits);
    }
}
