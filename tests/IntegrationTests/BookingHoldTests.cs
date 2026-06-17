using System.Collections.Concurrent;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.BuildingBlocks;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The booking hold saga end-to-end (Gate G1): quote + atomic hold + persist in one transaction,
/// idempotent, and — the decisive property — no overbooking under concurrent holds.
/// </summary>
public sealed class BookingHoldTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private const long PropertyId = 99;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();
    private BookingHoldService _saga = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _saga = new BookingHoldService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAvailabilityAsync(int allotment, decimal price = 100m)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, allotment);
        await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, CheckIn, CheckOut, price, "SGD");
        await tx.CommitAsync();
    }

    private static HoldRequest Request(string? key = null, int quantity = 1) => new(
        IdempotencyKey: key ?? Guid.NewGuid().ToString("N"),
        GuestId: 1, ContactEmail: "guest@example.com", PropertyId: PropertyId,
        RoomTypeId: RoomTypeId, RatePlanId: RatePlanId, CheckIn: CheckIn, CheckOut: CheckOut,
        Quantity: quantity, Adults: 2, Children: 0, HoldTtl: TimeSpan.FromMinutes(15));

    private async Task<T> ScalarAsync<T>(string sql, object? p = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, p))!;
    }

    [Fact]
    public async Task Hold_prices_holds_and_persists_a_held_booking()
    {
        await SeedAvailabilityAsync(allotment: 5, price: 100m);

        var result = await _saga.HoldAsync(Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        var hold = result.Value!;
        Assert.Equal("HELD", hold.Status);
        Assert.Equal("SGD", hold.Currency);
        Assert.Equal(300m, hold.TotalAmount);             // 3 nights × 100
        Assert.NotNull(hold.HoldExpiresAt);

        // Booking row, frozen breakdown, per-night hold rows, status history, and outbox event all written.
        Assert.Equal("HELD", await ScalarAsync<string>(
            "SELECT status FROM booking.booking WHERE id = @Id", new { Id = hold.BookingId }));
        Assert.Equal(3, await ScalarAsync<int>(
            "SELECT count(*) FROM booking.inventory_hold WHERE booking_id = @Id", new { Id = hold.BookingId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*) FROM booking.status_history WHERE booking_id = @Id AND to_status = 'HELD'", new { Id = hold.BookingId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*) FROM booking.outbox_message WHERE type = 'stay.booking.held'"));

        // Inventory reflects the hold on every night.
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT units_held FROM ari.inventory_calendar WHERE room_type_id = @RoomTypeId AND stay_date = @d",
            new { RoomTypeId, d = CheckIn }));
    }

    [Fact]
    public async Task Hold_is_sold_out_when_inventory_is_insufficient()
    {
        await SeedAvailabilityAsync(allotment: 0, price: 100m);

        var result = await _saga.HoldAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal("sold-out", result.Error!.Value.Code);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM booking.booking"));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT coalesce(sum(units_held),0) FROM ari.inventory_calendar WHERE room_type_id = @RoomTypeId", new { RoomTypeId }));
    }

    [Fact]
    public async Task Hold_is_price_unavailable_when_a_night_has_no_rate()
    {
        // Allotment exists but no rates were set → cannot freeze a price.
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, 5);
            await tx.CommitAsync();
        }

        var result = await _saga.HoldAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal("price-unavailable", result.Error!.Value.Code);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM booking.booking"));
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_returns_the_same_booking()
    {
        await SeedAvailabilityAsync(allotment: 5, price: 100m);
        var request = Request(key: "retry-123");

        var first = await _saga.HoldAsync(request);
        var second = await _saga.HoldAsync(request);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.BookingId, second.Value!.BookingId);  // same booking, not a duplicate
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM booking.booking"));
        Assert.Equal(1, await ScalarAsync<int>(                          // not double-held
            "SELECT units_held FROM ari.inventory_calendar WHERE room_type_id = @RoomTypeId AND stay_date = @d",
            new { RoomTypeId, d = CheckIn }));
    }

    [Fact]
    public async Task Concurrent_distinct_holds_never_oversell()
    {
        const int allotment = 5;
        const int attempts = 20;
        await SeedAvailabilityAsync(allotment, price: 100m);

        var results = new ConcurrentBag<Result<HoldResult>>();
        await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
            results.Add(await _saga.HoldAsync(Request()))));

        // Exactly `allotment` holds succeed; the inventory and booking counts agree — no overbooking.
        Assert.Equal(allotment, results.Count(r => r.IsSuccess));
        Assert.Equal(attempts - allotment, results.Count(r => !r.IsSuccess && r.Error!.Value.Code == "sold-out"));
        Assert.Equal(allotment, await ScalarAsync<int>("SELECT count(*) FROM booking.booking WHERE status = 'HELD'"));
        Assert.Equal(allotment, await ScalarAsync<int>(
            "SELECT units_held FROM ari.inventory_calendar WHERE room_type_id = @RoomTypeId AND stay_date = @d",
            new { RoomTypeId, d = CheckIn }));
    }
}
