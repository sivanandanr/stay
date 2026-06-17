using System.Collections.Concurrent;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The no-overbooking inventory core (BR-1, Gate G1) against real Postgres, including the decisive
/// concurrency proof: parallel holds against limited allotment never oversell.
/// </summary>
public sealed class InventoryHoldTests : IAsyncLifetime
{
    private const long RoomTypeId = 1;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights: 10,11,12

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly InventoryRepository _inventory = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAsync(int allotment, DateOnly from, DateOnly toExclusive)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, from, toExclusive, allotment);
        await tx.CommitAsync();
    }

    /// <summary>Opens its own connection/transaction and commits a hold only when it succeeds.</summary>
    private async Task<HoldOutcome> HoldAsync(DateOnly checkIn, DateOnly checkOut, int qty)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var outcome = await _inventory.TryHoldAsync(conn, tx, RoomTypeId, checkIn, checkOut, qty);
        if (outcome == HoldOutcome.Held)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return outcome;
    }

    private async Task<int> HeldOnAsync(DateOnly date)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT units_held FROM ari.inventory_calendar WHERE room_type_id = @RoomTypeId AND stay_date = @date",
            new { RoomTypeId, date });
    }

    [Fact]
    public async Task Hold_succeeds_when_inventory_is_available()
    {
        await SeedAsync(5, CheckIn, CheckOut);

        Assert.Equal(HoldOutcome.Held, await HoldAsync(CheckIn, CheckOut, 2));
        Assert.Equal(2, await HeldOnAsync(CheckIn));
        Assert.Equal(2, await HeldOnAsync(new DateOnly(2030, 6, 12)));
    }

    [Fact]
    public async Task Hold_is_sold_out_when_a_single_night_lacks_inventory()
    {
        // All three nights have 5, except the middle night has 1.
        await SeedAsync(5, CheckIn, CheckOut);
        await SeedAsync(1, new DateOnly(2030, 6, 11), new DateOnly(2030, 6, 12));

        // Requesting 2 over the range fails because night 11 only has 1 — all-nights-or-none.
        Assert.Equal(HoldOutcome.SoldOut, await HoldAsync(CheckIn, CheckOut, 2));

        // Nothing was held on any night (the partial hold was rolled back).
        Assert.Equal(0, await HeldOnAsync(CheckIn));
        Assert.Equal(0, await HeldOnAsync(new DateOnly(2030, 6, 11)));
        Assert.Equal(0, await HeldOnAsync(new DateOnly(2030, 6, 12)));
    }

    [Fact]
    public async Task Release_returns_units_to_availability()
    {
        await SeedAsync(3, CheckIn, CheckOut);
        await HoldAsync(CheckIn, CheckOut, 3);
        Assert.Equal(HoldOutcome.SoldOut, await HoldAsync(CheckIn, CheckOut, 1)); // full

        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await _inventory.ReleaseAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, 3);
            await tx.CommitAsync();
        }

        Assert.Equal(HoldOutcome.Held, await HoldAsync(CheckIn, CheckOut, 1)); // available again
    }

    [Fact]
    public async Task Parallel_holds_never_oversell_a_single_night()
    {
        const int allotment = 5;
        const int attempts = 25;
        var night = new DateOnly(2030, 6, 20);
        await SeedAsync(allotment, night, night.AddDays(1));

        var outcomes = new ConcurrentBag<HoldOutcome>();
        await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
            outcomes.Add(await HoldAsync(night, night.AddDays(1), 1))));

        // Exactly `allotment` holds succeed; the rest are sold out; the DB CHECK is never breached.
        Assert.Equal(allotment, outcomes.Count(o => o == HoldOutcome.Held));
        Assert.Equal(attempts - allotment, outcomes.Count(o => o == HoldOutcome.SoldOut));
        Assert.Equal(allotment, await HeldOnAsync(night));
    }
}
