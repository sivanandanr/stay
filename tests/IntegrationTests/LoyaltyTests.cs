using Dapper;
using Npgsql;
using Stay.Loyalty.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 9 — loyalty points: earn and redeem are idempotent by key (BR-5), redemption can't overdraw
/// (checked + DB backstop), and the balance + ledger stay consistent.
/// </summary>
public sealed class LoyaltyTests : IAsyncLifetime
{
    private const long GuestId = 7;

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private LoyaltyService _loyalty = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(LoyaltySchema.Ddl);
        _loyalty = new LoyaltyService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<int> LedgerCountAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM loyalty.ledger");
    }

    [Fact]
    public async Task Earning_credits_the_balance()
    {
        var result = await _loyalty.EarnAsync(GuestId, 500, "earn:booking:1", "stay completed");

        Assert.True(result.IsSuccess);
        Assert.Equal(500, result.Value!.Balance);
        Assert.Equal(500, (await _loyalty.GetAsync(GuestId)).Balance);
    }

    [Fact]
    public async Task Earning_is_idempotent_by_key()
    {
        await _loyalty.EarnAsync(GuestId, 500, "earn:booking:1");
        var second = await _loyalty.EarnAsync(GuestId, 500, "earn:booking:1"); // replay

        Assert.Equal(500, second.Value!.Balance); // credited once
        Assert.Equal(1, await LedgerCountAsync());
    }

    [Fact]
    public async Task Redeeming_debits_the_balance()
    {
        await _loyalty.EarnAsync(GuestId, 500, "earn:1");

        var result = await _loyalty.RedeemAsync(GuestId, 200, "redeem:1", "discount");

        Assert.Equal(300, result.Value!.Balance);
    }

    [Fact]
    public async Task Redeeming_more_than_the_balance_is_rejected()
    {
        await _loyalty.EarnAsync(GuestId, 100, "earn:1");

        var result = await _loyalty.RedeemAsync(GuestId, 250, "redeem:1");

        Assert.False(result.IsSuccess);
        Assert.Equal("insufficient-points", result.Error!.Value.Code);
        Assert.Equal(100, (await _loyalty.GetAsync(GuestId)).Balance); // unchanged
    }

    [Fact]
    public async Task Redeeming_is_idempotent_by_key()
    {
        await _loyalty.EarnAsync(GuestId, 500, "earn:1");

        await _loyalty.RedeemAsync(GuestId, 200, "redeem:1");
        var second = await _loyalty.RedeemAsync(GuestId, 200, "redeem:1"); // replay

        Assert.Equal(300, second.Value!.Balance); // debited once
    }

    [Fact]
    public async Task An_unknown_guest_has_a_zero_bronze_balance()
    {
        var account = await _loyalty.GetAsync(999_999);

        Assert.Equal(0, account.Balance);
        Assert.Equal("BRONZE", account.Tier);
    }

    [Fact]
    public async Task Non_positive_amounts_are_rejected()
    {
        Assert.Equal("validation", (await _loyalty.EarnAsync(GuestId, 0, "k1")).Error!.Value.Code);
        Assert.Equal("validation", (await _loyalty.RedeemAsync(GuestId, -5, "k2")).Error!.Value.Code);
    }

    [Fact]
    public async Task Tier_rises_with_lifetime_earnings()
    {
        Assert.Equal("BRONZE", (await _loyalty.EarnAsync(GuestId, 500, "e1")).Value!.Tier);
        Assert.Equal("SILVER", (await _loyalty.EarnAsync(GuestId, 600, "e2")).Value!.Tier);   // 1100 lifetime
        Assert.Equal("GOLD", (await _loyalty.EarnAsync(GuestId, 4000, "e3")).Value!.Tier);    // 5100 lifetime
    }

    [Fact]
    public async Task Redeeming_never_lowers_the_tier()
    {
        await _loyalty.EarnAsync(GuestId, 1200, "e1"); // SILVER

        var afterRedeem = await _loyalty.RedeemAsync(GuestId, 1000, "r1");

        Assert.Equal("SILVER", afterRedeem.Value!.Tier); // tier tracks lifetime earned, not balance
        Assert.Equal("SILVER", (await _loyalty.GetAsync(GuestId)).Tier);
    }

    [Fact]
    public void Tier_thresholds_are_monotonic()
    {
        Assert.Equal("BRONZE", LoyaltyService.TierFor(0));
        Assert.Equal("SILVER", LoyaltyService.TierFor(1_000));
        Assert.Equal("GOLD", LoyaltyService.TierFor(5_000));
        Assert.Equal("PLATINUM", LoyaltyService.TierFor(20_000));
    }
}
