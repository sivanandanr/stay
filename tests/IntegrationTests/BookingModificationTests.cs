using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.BuildingBlocks;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Stay.Loyalty.Infrastructure;
using Stay.Payment.Infrastructure;
using Stay.Promotion.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Booking modification (Phase 5): re-quote new dates, move inventory atomically, update totals.</summary>
public sealed class BookingModificationTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private static readonly DateOnly OldIn = new(2030, 6, 10);
    private static readonly DateOnly OldOut = new(2030, 6, 13);   // 3 nights
    private static readonly DateOnly NewIn = new(2030, 6, 17);
    private static readonly DateOnly NewOut = new(2030, 6, 21);   // 4 nights

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();
    private BookingHoldService _hold = null!;
    private ModifyBookingService _modify = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        await conn.ExecuteAsync(BookingSchema.Ddl);
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _hold = new BookingHoldService(_postgres.GetConnectionString(), new PromotionService(_postgres.GetConnectionString()), new LoyaltyService(_postgres.GetConnectionString()));
        _modify = new ModifyBookingService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAsync(DateOnly from, DateOnly toExclusive, int allotment = 5, decimal price = 100m)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, from, toExclusive, allotment);
        await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, from, toExclusive, price, "SGD");
        await tx.CommitAsync();
    }

    private async Task<long> ConfirmedOnOldDatesAsync()
    {
        var held = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), 1, "g@example.com", 99, RoomTypeId, RatePlanId,
            OldIn, OldOut, 1, 2, 0, TimeSpan.FromMinutes(15)));
        var bookingId = held.Value!.BookingId;
        await new BookingConfirmService(_postgres.GetConnectionString(), new FakePaymentGateway(), new PromotionService(_postgres.GetConnectionString()), new LoyaltyService(_postgres.GetConnectionString())).ConfirmAsync(bookingId);
        return bookingId;
    }

    private async Task<int> SoldOnAsync(DateOnly date)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT coalesce((SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@date), 0)",
            new { RoomTypeId, date });
    }

    [Fact]
    public async Task Modify_moves_inventory_and_reprices()
    {
        await SeedAsync(OldIn, OldOut);
        await SeedAsync(NewIn, NewOut);
        var bookingId = await ConfirmedOnOldDatesAsync();
        Assert.Equal(1, await SoldOnAsync(OldIn));

        var result = await _modify.ModifyAsync(bookingId, NewIn, NewOut);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(400m, result.Value!.NewTotal);  // 4 nights × 100
        Assert.Equal(100m, result.Value!.Delta);      // was 300

        Assert.Equal(0, await SoldOnAsync(OldIn));     // old nights released
        Assert.Equal(1, await SoldOnAsync(NewIn));     // new nights sold
        Assert.Equal(1, await SoldOnAsync(new DateOnly(2030, 6, 20)));

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        Assert.Equal(NewIn, await conn.ExecuteScalarAsync<DateOnly>(
            "SELECT check_in FROM booking.booking_room WHERE booking_id=@Id", new { Id = bookingId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM booking.outbox_message WHERE type='stay.booking.modified'"));
    }

    [Fact]
    public async Task Modify_to_sold_out_dates_leaves_the_booking_unchanged()
    {
        await SeedAsync(OldIn, OldOut);
        await SeedAsync(NewIn, NewOut, allotment: 0); // no availability on the new dates
        var bookingId = await ConfirmedOnOldDatesAsync();

        var result = await _modify.ModifyAsync(bookingId, NewIn, NewOut);

        Assert.False(result.IsSuccess);
        Assert.Equal("sold-out", result.Error!.Value.Code);
        Assert.Equal(1, await SoldOnAsync(OldIn));   // original nights still sold
        Assert.Equal(0, await SoldOnAsync(NewIn));
    }

    [Fact]
    public async Task Modify_to_unpriced_dates_is_rejected()
    {
        await SeedAsync(OldIn, OldOut);
        // New dates have allotment but no rates.
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, NewIn, NewOut, 5);
            await tx.CommitAsync();
        }
        var bookingId = await ConfirmedOnOldDatesAsync();

        var result = await _modify.ModifyAsync(bookingId, NewIn, NewOut);

        Assert.False(result.IsSuccess);
        Assert.Equal("price-unavailable", result.Error!.Value.Code);
        Assert.Equal(1, await SoldOnAsync(OldIn)); // unchanged
    }

    [Fact]
    public async Task Modify_rejects_a_booking_that_is_not_confirmed()
    {
        await SeedAsync(OldIn, OldOut);
        await SeedAsync(NewIn, NewOut);
        var held = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), 1, "g@example.com", 99, RoomTypeId, RatePlanId,
            OldIn, OldOut, 1, 2, 0, TimeSpan.FromMinutes(15)));

        var result = await _modify.ModifyAsync(held.Value!.BookingId, NewIn, NewOut); // still HELD

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid-state", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Modify_is_tenancy_scoped()
    {
        await SeedAsync(OldIn, OldOut);
        await SeedAsync(NewIn, NewOut);
        var bookingId = await ConfirmedOnOldDatesAsync(); // guest_id 1

        var result = await _modify.ModifyAsync(bookingId, NewIn, NewOut, requireGuestId: 999);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking-not-found", result.Error!.Value.Code);
        Assert.Equal(1, await SoldOnAsync(OldIn)); // untouched
    }
}
