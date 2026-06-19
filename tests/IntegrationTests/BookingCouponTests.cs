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
/// Phase 9 funnel redemption — a coupon discounts the FROZEN quote at hold (BR-2) and is redeemed
/// atomically in the confirm transaction. The in-saga redemption prevents over-budget use even when
/// two holds were both quoted the discount (the guarantee behind the atomic-in-saga choice).
/// </summary>
public sealed class BookingCouponTests : IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        await conn.ExecuteAsync(BookingSchema.Ddl);
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        await conn.ExecuteAsync(PromotionSchema.Ddl);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, 5);
            await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, CheckIn, CheckOut, 100m, "INR");
            await tx.CommitAsync();
        }

        var cs = _postgres.GetConnectionString();
        _hold = new BookingHoldService(cs, new PromotionService(cs), new LoyaltyService(cs));
        _confirm = new BookingConfirmService(cs, new FakePaymentGateway(), new PromotionService(cs), new LoyaltyService(cs));
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Creates a 10%-off coupon with an optional budget cap.</summary>
    private async Task SeedCouponAsync(string code, decimal? budget = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var promotionId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO promotion.promotion (owner_type, name, type, conditions, effect, budget, status)
            VALUES ('PLATFORM', 'Sale', 'PERCENT_OFF', '{}'::jsonb, '{"value": 10}'::jsonb, @budget, 'ACTIVE')
            RETURNING id
            """, new { budget });
        await conn.ExecuteAsync(
            "INSERT INTO promotion.coupon (promotion_id, code) VALUES (@promotionId, @code)",
            new { promotionId, code });
    }

    private async Task<long> HoldAsync(string code) =>
        (await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), 1, "g@x.io", 99, RoomTypeId, RatePlanId,
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15), CouponCode: code))).Value!.BookingId;

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task A_coupon_discounts_the_frozen_total_at_hold()
    {
        await SeedCouponAsync("SAVE10");

        var held = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), 1, "g@x.io", 99, RoomTypeId, RatePlanId,
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15), CouponCode: "SAVE10"));

        Assert.True(held.IsSuccess, held.Error?.Message);
        Assert.Equal(270m, held.Value!.TotalAmount); // 300 - 10%
    }

    [Fact]
    public async Task Confirm_redeems_the_coupon_atomically()
    {
        await SeedCouponAsync("SAVE10");
        var bookingId = await HoldAsync("SAVE10");

        var confirmed = await _confirm.ConfirmAsync(bookingId);

        Assert.True(confirmed.IsSuccess, confirmed.Error?.Message);
        Assert.Equal(30m, await ScalarAsync<decimal>(
            "SELECT amount FROM promotion.coupon_redemption WHERE booking_id = @bookingId", new { bookingId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT redeemed_count FROM promotion.coupon WHERE code = 'SAVE10'", new { }));
    }

    [Fact]
    public async Task In_saga_redemption_prevents_over_budget_use()
    {
        await SeedCouponAsync("TIGHT", budget: 30m); // enough for exactly one 30-discount redemption
        var a = await HoldAsync("TIGHT");
        var b = await HoldAsync("TIGHT"); // both quoted the discount (no redemption committed yet)

        var confirmA = await _confirm.ConfirmAsync(a);
        var confirmB = await _confirm.ConfirmAsync(b);

        Assert.True(confirmA.IsSuccess);
        Assert.False(confirmB.IsSuccess);                                   // budget already spent by A
        Assert.Equal("budget-exhausted", confirmB.Error!.Value.Code);
        Assert.Equal("HELD", await ScalarAsync<string>(
            "SELECT status FROM booking.booking WHERE id = @b", new { b })); // B rolled back, not confirmed
    }

    [Fact]
    public async Task Confirm_is_idempotent_and_redeems_once()
    {
        await SeedCouponAsync("SAVE10");
        var bookingId = await HoldAsync("SAVE10");

        await _confirm.ConfirmAsync(bookingId);
        await _confirm.ConfirmAsync(bookingId); // replay

        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::int FROM promotion.coupon_redemption WHERE booking_id = @bookingId", new { bookingId }));
    }
}
