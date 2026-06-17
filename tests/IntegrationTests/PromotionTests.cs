using Dapper;
using Npgsql;
using Stay.Promotion.Contracts;
using Stay.Promotion.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 — coupons: preview computes the discount honestly, redemption is recorded once per booking
/// (idempotent, BR-2), and caps (window, minimum, max-redemptions, budget) are enforced.
/// </summary>
public sealed class PromotionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2030, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private PromotionService _promotions = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PromotionSchema.Ddl);
        _promotions = new PromotionService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<string> CouponAsync(
        CreatePromotionRequest promo, string code, int? maxRedemptions = null)
    {
        var p = (await _promotions.CreateAsync(promo)).Value!;
        await _promotions.IssueCouponAsync(p.Id, new IssueCouponRequest(code, maxRedemptions, null));
        return code;
    }

    private static CreatePromotionRequest Percent(decimal pct, decimal? min = null, decimal? budget = null) =>
        new("PLATFORM", null, "Sale", "PERCENT_OFF", pct, min, budget, null, null);

    private async Task<int> RedeemedCountAsync(string code)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT redeemed_count FROM promotion.coupon WHERE code = @code", new { code });
    }

    [Fact]
    public async Task A_percent_coupon_discounts_the_amount()
    {
        var code = await CouponAsync(Percent(10m), "SAVE10");

        var quote = await _promotions.ApplyAsync(code, 2000.00m, "INR", Now);

        Assert.True(quote.IsSuccess);
        Assert.Equal(200.00m, quote.Value!.DiscountAmount);
        Assert.Equal(1800.00m, quote.Value.NetAmount);
    }

    [Fact]
    public async Task A_fixed_coupon_never_discounts_below_zero()
    {
        var p = (await _promotions.CreateAsync(
            new CreatePromotionRequest("PLATFORM", null, "Flat", "FIXED_OFF", 500m, null, null, null, null))).Value!;
        await _promotions.IssueCouponAsync(p.Id, new IssueCouponRequest("FLAT500", null, null));

        var quote = await _promotions.ApplyAsync("FLAT500", 300.00m, "INR", Now);

        Assert.Equal(300.00m, quote.Value!.DiscountAmount); // capped at the amount
        Assert.Equal(0.00m, quote.Value.NetAmount);
    }

    [Fact]
    public async Task A_coupon_below_its_minimum_is_rejected()
    {
        var code = await CouponAsync(Percent(10m, min: 1000m), "MIN1000");

        var quote = await _promotions.ApplyAsync(code, 500.00m, "INR", Now);

        Assert.False(quote.IsSuccess);
        Assert.Equal("below-minimum", quote.Error!.Value.Code);
    }

    [Fact]
    public async Task A_coupon_outside_its_window_is_rejected()
    {
        var promo = new CreatePromotionRequest("PLATFORM", null, "Future", "PERCENT_OFF", 10m, null, null,
            ValidFrom: Now.AddDays(1), ValidTo: Now.AddDays(5));
        var code = await CouponAsync(promo, "FUTURE");

        var quote = await _promotions.ApplyAsync(code, 1000.00m, "INR", Now);

        Assert.False(quote.IsSuccess);
        Assert.Equal("outside-window", quote.Error!.Value.Code);
    }

    [Fact]
    public async Task Redemption_is_recorded_once_per_booking()
    {
        var code = await CouponAsync(Percent(10m), "ONCE");

        var first = await _promotions.RedeemAsync(code, bookingId: 5001, guestId: 7, 2000.00m, "INR", Now);
        var second = await _promotions.RedeemAsync(code, bookingId: 5001, guestId: 7, 2000.00m, "INR", Now);

        Assert.Equal(200.00m, first.Value!.DiscountAmount);
        Assert.Equal(200.00m, second.Value!.DiscountAmount); // same recorded amount
        Assert.Equal(1, await RedeemedCountAsync(code));      // counted once
    }

    [Fact]
    public async Task A_coupon_at_its_redemption_limit_is_rejected()
    {
        var code = await CouponAsync(Percent(10m), "ONLY1", maxRedemptions: 1);
        await _promotions.RedeemAsync(code, bookingId: 1, guestId: 1, 1000.00m, "INR", Now);

        var quote = await _promotions.ApplyAsync(code, 1000.00m, "INR", Now);

        Assert.False(quote.IsSuccess);
        Assert.Equal("max-redemptions-reached", quote.Error!.Value.Code);
    }

    [Fact]
    public async Task A_coupon_exhausting_its_budget_is_rejected()
    {
        var code = await CouponAsync(Percent(50m, budget: 300m), "BUDGET");
        await _promotions.RedeemAsync(code, bookingId: 1, guestId: 1, 400.00m, "INR", Now); // uses 200

        // A second redemption would add another 200 → 400 > 300 budget.
        var quote = await _promotions.ApplyAsync(code, 400.00m, "INR", Now);

        Assert.False(quote.IsSuccess);
        Assert.Equal("budget-exhausted", quote.Error!.Value.Code);
    }

    [Fact]
    public async Task A_duplicate_coupon_code_is_rejected()
    {
        var p = (await _promotions.CreateAsync(Percent(10m))).Value!;
        await _promotions.IssueCouponAsync(p.Id, new IssueCouponRequest("DUP", null, null));

        var second = await _promotions.IssueCouponAsync(p.Id, new IssueCouponRequest("DUP", null, null));

        Assert.False(second.IsSuccess);
        Assert.Equal("coupon-code-taken", second.Error!.Value.Code);
    }
}
