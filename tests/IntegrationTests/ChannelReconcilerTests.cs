using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Channel.Contracts;
using Stay.Channel.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 7 / Gate G5 — reconciliation opens an OVERBOOK conflict when the channel offers more than we
/// can honor, and never duplicates an already-open one (so it's safe to schedule repeatedly).
/// </summary>
public sealed class ChannelReconcilerTests : IAsyncLifetime
{
    private const long RoomTypeId = 7001;
    private const long PropertyId = 55;
    private static readonly DateOnly From = new(2030, 6, 10);
    private static readonly DateOnly To = new(2030, 6, 12); // 2 nights

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private FakeChannelClient _client = null!;
    private ChannelReconciler _reconciler = null!;
    private long _connectionId;

    public async Task InitializeAsync()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler()); // seed below binds DateOnly params
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(ChannelSchema.Ddl);

        _connectionId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO channel.channel_connection (property_id, provider, credentials_ref)
            VALUES (@PropertyId, 'STAAH', 'secret://x') RETURNING id
            """, new { PropertyId });
        await conn.ExecuteAsync("""
            INSERT INTO channel.room_mapping (channel_connection_id, external_room_code, room_type_id)
            VALUES (@c, 'DLX', @rt)
            """, new { c = _connectionId, rt = RoomTypeId });

        // Our true availability = 5 each night (allotment 5, nothing sold/held).
        await conn.ExecuteAsync("""
            INSERT INTO ari.inventory_calendar (room_type_id, stay_date, total_allotment)
            SELECT @RoomTypeId, gs::date, 5
            FROM generate_series(@From::date, @To::date - 1, interval '1 day') AS gs
            """, new { RoomTypeId, From, To });

        _client = new FakeChannelClient();
        _reconciler = new ChannelReconciler(_postgres.GetConnectionString(), _client);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<int> OpenConflictsAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM channel.sync_conflict WHERE status = 'OPEN' AND type = 'OVERBOOK'");
    }

    [Fact]
    public async Task Channel_offering_more_than_we_can_honor_opens_an_overbook_conflict()
    {
        _client.SetChannelAvailability(_connectionId, "DLX", From, 8);            // channel says 8, we have 5
        _client.SetChannelAvailability(_connectionId, "DLX", From.AddDays(1), 8);

        var result = await _reconciler.ReconcileAsync(_connectionId, From, To);

        Assert.Equal(2, result.Value!.NightsChecked);
        Assert.Equal(2, result.Value.ConflictsOpened.Count);                      // one per night
        Assert.All(result.Value.ConflictsOpened, c => Assert.Equal("OVERBOOK", c.Type));
        Assert.Equal(2, await OpenConflictsAsync());
    }

    [Fact]
    public async Task Reconciliation_is_idempotent_for_an_already_open_conflict()
    {
        _client.SetChannelAvailability(_connectionId, "DLX", From, 9);
        _client.SetChannelAvailability(_connectionId, "DLX", From.AddDays(1), 9);

        await _reconciler.ReconcileAsync(_connectionId, From, To);
        var second = await _reconciler.ReconcileAsync(_connectionId, From, To);   // run again

        Assert.Empty(second.Value!.ConflictsOpened);                             // nothing new opened
        Assert.Equal(2, await OpenConflictsAsync());                            // still exactly 2
    }

    [Fact]
    public async Task No_conflict_when_the_channel_is_within_our_availability()
    {
        _client.SetChannelAvailability(_connectionId, "DLX", From, 5);
        _client.SetChannelAvailability(_connectionId, "DLX", From.AddDays(1), 3);

        var result = await _reconciler.ReconcileAsync(_connectionId, From, To);

        Assert.Empty(result.Value!.ConflictsOpened);
        Assert.Equal(0, await OpenConflictsAsync());
    }
}
