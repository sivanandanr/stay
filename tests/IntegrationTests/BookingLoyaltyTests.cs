using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Stay.Loyalty.Infrastructure;
using Stay.Payment.Infrastructure;
using Stay.Promotion.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 9 funnel redemption (loyalty) — points discount the FROZEN quote at hold (BR-2) and the points
/// are decremented atomically in the confirm transaction. The in-saga redemption rolls the whole confirm
/// back if the guest no longer has the points, and the discount is capped so it never exceeds the bill.
/// Mirrors <see cref="BookingCouponTests"/>; the two redemptions stack on one booking.
/// </summary>
public sealed class BookingLoyaltyTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private const long GuestId = 1;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights @ 100 = 300

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();
    private LoyaltyService _loyalty = null!;
    private BookingHoldService _hold = null!;
    private BookingConfirmService _confirm = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        await conn.ExecuteAsync(BookingSchema.Ddl);
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        await conn.ExecuteAsync(PromotionSchema.Ddl);
        await conn.ExecuteAsync(LoyaltySchema.Ddl);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, 5);
            await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, CheckIn, CheckOut, 100m, "INR");
            await tx.CommitAsync();
        }

        var cs = _postgres.GetConnectionString();
        _loyalty = new LoyaltyService(cs);
        _hold = new BookingHoldService(cs, new PromotionService(cs), _loyalty);
        _confirm = new BookingConfirmService(cs, new FakePaymentGateway(), new PromotionService(cs), _loyalty);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a loyalty account with an exact balance for the booking guest.</summary>
    private async Task SeedBalanceAsync(int balance)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO loyalty.account (guest_id, balance, tier) VALUES (@GuestId, @balance, 'SILVER')",
            new { GuestId, balance });
    }

    private async Task<long> HoldAsync(int redeemPoints) =>
        (await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), GuestId, "g@x.io", 99, RoomTypeId, RatePlanId,
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15), RedeemPoints: redeemPoints))).Value!.BookingId;

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task Points_discount_the_frozen_total_at_hold()
    {
        await SeedBalanceAsync(100);

        var held = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), GuestId, "g@x.io", 99, RoomTypeId, RatePlanId,
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15), RedeemPoints: 50));

        Assert.True(held.IsSuccess, held.Error?.Message);
        Assert.Equal(250m, held.Value!.TotalAmount); // 300 − 50 points × ₹1
        Assert.Equal(50, await ScalarAsync<int>(
            "SELECT points_redeemed FROM booking.booking WHERE id = @id", new { id = held.Value!.BookingId }));
    }

    [Fact]
    public async Task Confirm_redeems_the_points_atomically()
    {
        await SeedBalanceAsync(100);
        var bookingId = await HoldAsync(50);

        var confirmed = await _confirm.ConfirmAsync(bookingId);

        Assert.True(confirmed.IsSuccess, confirmed.Error?.Message);
        Assert.Equal(50, await ScalarAsync<int>(
            "SELECT balance FROM loyalty.account WHERE guest_id = @GuestId", new { GuestId })); // 100 − 50
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::int FROM loyalty.ledger WHERE type = 'REDEEM'", new { }));
    }

    [Fact]
    public async Task Confirm_is_idempotent_and_redeems_once()
    {
        await SeedBalanceAsync(100);
        var bookingId = await HoldAsync(50);

        await _confirm.ConfirmAsync(bookingId);
        await _confirm.ConfirmAsync(bookingId); // replay

        Assert.Equal(50, await ScalarAsync<int>(
            "SELECT balance FROM loyalty.account WHERE guest_id = @GuestId", new { GuestId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::int FROM loyalty.ledger WHERE type = 'REDEEM'", new { }));
    }

    [Fact]
    public async Task In_saga_redemption_rolls_back_when_points_were_spent_after_hold()
    {
        await SeedBalanceAsync(50);
        var bookingId = await HoldAsync(50); // the discount (50) is frozen onto the booking

        // The guest spends the points elsewhere before confirming → the saga can no longer redeem.
        await _loyalty.RedeemAsync(GuestId, 50, "spent-elsewhere");

        var confirmed = await _confirm.ConfirmAsync(bookingId);

        Assert.False(confirmed.IsSuccess);
        Assert.Equal("insufficient-points", confirmed.Error!.Value.Code);
        Assert.Equal("HELD", await ScalarAsync<string>(
            "SELECT status FROM booking.booking WHERE id = @bookingId", new { bookingId })); // rolled back
    }

    [Fact]
    public async Task Redemption_is_capped_so_the_discount_never_exceeds_the_bill()
    {
        await SeedBalanceAsync(1000);

        var held = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), GuestId, "g@x.io", 99, RoomTypeId, RatePlanId,
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15), RedeemPoints: 1000)); // way more than the 300 bill

        Assert.True(held.IsSuccess, held.Error?.Message);
        Assert.Equal(0m, held.Value!.TotalAmount);               // capped at the 300 bill → total 0
        Assert.Equal(300, await ScalarAsync<int>(
            "SELECT points_redeemed FROM booking.booking WHERE id = @id", new { id = held.Value!.BookingId }));

        var confirmed = await _confirm.ConfirmAsync(held.Value!.BookingId);
        Assert.True(confirmed.IsSuccess, confirmed.Error?.Message);
        Assert.Equal(700, await ScalarAsync<int>(                  // only the capped 300 were spent
            "SELECT balance FROM loyalty.account WHERE guest_id = @GuestId", new { GuestId }));
    }
}
