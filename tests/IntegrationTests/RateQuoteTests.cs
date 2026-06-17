using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Pricing;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>The deterministic quote pipeline (BR-2) over the partitioned rate calendar.</summary>
public sealed class RateQuoteTests : IAsyncLifetime
{
    private const long RoomTypeId = 1;
    private const long RatePlanId = 1;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly RateRepository _rates = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SetRateAsync(
        DateOnly from, DateOnly toExclusive, decimal price, string currency = "SGD",
        IReadOnlyDictionary<int, decimal>? occupancyPrices = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, from, toExclusive, price, currency, occupancyPrices);
        await tx.CommitAsync();
    }

    private async Task<Quote?> QuoteAsync(int occupancy = 2)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await _rates.QuoteAsync(conn, RoomTypeId, RatePlanId, CheckIn, CheckOut, occupancy);
    }

    [Fact]
    public async Task Quote_sums_the_per_night_prices()
    {
        await SetRateAsync(CheckIn, CheckOut, 100m);

        var quote = await QuoteAsync();

        Assert.NotNull(quote);
        Assert.Equal("SGD", quote!.Currency);
        Assert.Equal(300m, quote.Total);          // 3 nights × 100
        Assert.Equal(3, quote.Nights.Count);
        Assert.All(quote.Nights, n => Assert.Equal(100m, n.Price));
        Assert.Equal(CheckIn, quote.Nights[0].Date);
    }

    [Fact]
    public async Task Quote_reflects_per_night_rates()
    {
        // Night 10 = 100, nights 11–12 = 150 (a weekend bump).
        await SetRateAsync(CheckIn, CheckIn.AddDays(1), 100m);
        await SetRateAsync(CheckIn.AddDays(1), CheckOut, 150m);

        var quote = await QuoteAsync();

        Assert.NotNull(quote);
        Assert.Equal(400m, quote!.Total); // 100 + 150 + 150
    }

    [Fact]
    public async Task Quote_applies_the_occupancy_adjustment()
    {
        // Base 100 for double; +30 per night at occupancy 3.
        await SetRateAsync(CheckIn, CheckOut, 100m, occupancyPrices: new Dictionary<int, decimal> { [3] = 30m });

        var single = await QuoteAsync(occupancy: 2);     // no adjustment
        var triple = await QuoteAsync(occupancy: 3);     // +30/night

        Assert.Equal(300m, single!.Total);
        Assert.Equal(390m, triple!.Total);               // (100+30) × 3
    }

    [Fact]
    public async Task Quote_is_unavailable_when_a_night_has_no_rate()
    {
        // Only the first two nights are priced; night 12 is missing.
        await SetRateAsync(CheckIn, CheckIn.AddDays(2), 100m);

        Assert.Null(await QuoteAsync());
    }
}
