using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Stay.Payment.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Closing the hold lifecycle: confirm (commit inventory) and the expiry reaper (BR-3).</summary>
public sealed class BookingConfirmAndReaperTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();
    private BookingHoldService _hold = null!;
    private BookingConfirmService _confirm = null!;
    private HoldReaper _reaper = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        await conn.ExecuteAsync(BookingSchema.Ddl);
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _hold = new BookingHoldService(_postgres.GetConnectionString());
        _confirm = new BookingConfirmService(_postgres.GetConnectionString(), new FakePaymentGateway());
        _reaper = new HoldReaper(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAvailabilityAsync(int allotment = 5)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, allotment);
        await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, CheckIn, CheckOut, 100m, "SGD");
        await tx.CommitAsync();
    }

    private async Task<long> HoldAsync()
    {
        var result = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), GuestId: 1, ContactEmail: "g@example.com", PropertyId: 99,
            RoomTypeId: RoomTypeId, RatePlanId: RatePlanId, CheckIn: CheckIn, CheckOut: CheckOut,
            Quantity: 1, Adults: 2, Children: 0, HoldTtl: TimeSpan.FromMinutes(15)));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!.BookingId;
    }

    private async Task ExecAsync(string sql, object? p = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, p);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? p = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, p))!;
    }

    [Fact]
    public async Task Confirm_commits_inventory_and_marks_the_booking_confirmed()
    {
        await SeedAvailabilityAsync();
        var bookingId = await HoldAsync();

        var result = await _confirm.ConfirmAsync(bookingId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("CONFIRMED", result.Value!.Status);

        // Inventory moved held → sold on each night; hold rows released; event emitted.
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT units_held FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT count(*) FROM booking.inventory_hold WHERE booking_id=@Id AND released=false", new { Id = bookingId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM booking.outbox_message WHERE type='stay.booking.confirmed'"));
    }

    [Fact]
    public async Task Confirm_is_idempotent()
    {
        await SeedAvailabilityAsync();
        var bookingId = await HoldAsync();

        await _confirm.ConfirmAsync(bookingId);
        var second = await _confirm.ConfirmAsync(bookingId);

        Assert.True(second.IsSuccess);
        Assert.Equal("CONFIRMED", second.Value!.Status);
        Assert.Equal(1, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn })); // not double-committed
    }

    [Fact]
    public async Task Confirm_rejects_a_lapsed_hold()
    {
        await SeedAvailabilityAsync();
        var bookingId = await HoldAsync();
        await ExecAsync("UPDATE booking.booking SET hold_expires_at = now() - interval '1 minute' WHERE id=@Id", new { Id = bookingId });

        var result = await _confirm.ConfirmAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("hold-expired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Confirm_is_not_found_for_unknown_booking()
    {
        var result = await _confirm.ConfirmAsync(999_999);
        Assert.False(result.IsSuccess);
        Assert.Equal("booking-not-found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Reaper_expires_a_lapsed_hold_and_releases_inventory()
    {
        await SeedAvailabilityAsync();
        var bookingId = await HoldAsync();
        Assert.Equal(1, await ScalarAsync<int>("SELECT units_held FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));

        // Simulate the hold lapsing, then reap.
        await ExecAsync("UPDATE booking.booking SET hold_expires_at = now() - interval '1 minute' WHERE id=@Id", new { Id = bookingId });
        var reaped = await _reaper.ReapAsync();

        Assert.Equal(1, reaped);
        Assert.Equal("EXPIRED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_held FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn })); // released
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM booking.inventory_hold WHERE booking_id=@Id AND released=false", new { Id = bookingId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM booking.outbox_message WHERE type='stay.booking.expired'"));

        // Idempotent: a second pass finds nothing (the booking is no longer HELD) and releases nothing further.
        Assert.Equal(0, await _reaper.ReapAsync());
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_held FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
    }

    [Fact]
    public async Task Reaper_leaves_a_valid_hold_alone()
    {
        await SeedAvailabilityAsync();
        await HoldAsync(); // future expiry

        Assert.Equal(0, await _reaper.ReapAsync());
        Assert.Equal(1, await ScalarAsync<int>("SELECT units_held FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
    }
}
