using Dapper;
using Npgsql;
using Stay.Channel.Contracts;
using Stay.Channel.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 7 / Gate G5 — inbound ARI ingest is ordered (increasing seq only), idempotent (replays
/// drop), atomic (unmapped codes quarantine the whole message), and emits an outbox event on apply.
/// </summary>
public sealed class ChannelIngestTests : IAsyncLifetime
{
    private const long RoomTypeId = 7001;
    private const long RatePlanId = 9001;
    private const long PropertyId = 55;
    private static readonly DateOnly From = new(2030, 6, 10);
    private static readonly DateOnly ToExclusive = new(2030, 6, 13); // 3 nights

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private ChannelIngestService _ingest = null!;
    private long _connectionId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(ChannelSchema.Ddl);

        _connectionId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO channel.channel_connection (property_id, provider, credentials_ref)
            VALUES (@PropertyId, 'SITEMINDER', 'secret://x') RETURNING id
            """, new { PropertyId });

        // Map external room "DLX" → our room type; and "DLX"/"BAR" → a rate plan for rate updates.
        await conn.ExecuteAsync("""
            INSERT INTO channel.room_mapping (channel_connection_id, external_room_code, room_type_id, external_rate_code, rate_plan_id)
            VALUES (@c, 'DLX', @rt, NULL, NULL),
                   (@c, 'DLX', @rt, 'BAR', @rp)
            """, new { c = _connectionId, rt = RoomTypeId, rp = RatePlanId });

        _ingest = new ChannelIngestService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static AriIngestMessage Allotment(long seq, int units, string room = "DLX", string? rate = null) =>
        new(seq, [new AriUpdate(room, From, ToExclusive, units, null, null, rate)]);

    private async Task<int> AllotmentOnAsync(DateOnly date)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT total_allotment FROM ari.inventory_calendar WHERE room_type_id = @RoomTypeId AND stay_date = @date",
            new { RoomTypeId, date });
    }

    private async Task<(int Applied, int Stale, int Quarantined)> LogCountsAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var rows = (await conn.QueryAsync<(string Status, int N)>(
            "SELECT status AS Status, count(*)::int AS N FROM channel.ari_sync_log GROUP BY status")).ToList();
        int Count(string s) => rows.FirstOrDefault(r => r.Status == s).N;
        return (Count("APPLIED"), Count("DROPPED_STALE"), Count("QUARANTINED"));
    }

    [Fact]
    public async Task Increasing_sequences_apply_in_order()
    {
        var first = await _ingest.IngestAsync(_connectionId, Allotment(1, 5));
        var second = await _ingest.IngestAsync(_connectionId, Allotment(2, 8));

        Assert.Equal(IngestOutcome.Applied, first.Value!.Outcome);
        Assert.Equal(IngestOutcome.Applied, second.Value!.Outcome);
        Assert.Equal(8, await AllotmentOnAsync(From));            // latest wins on every night
        Assert.Equal(8, await AllotmentOnAsync(new DateOnly(2030, 6, 12)));
    }

    [Fact]
    public async Task A_stale_sequence_is_dropped_and_does_not_overwrite()
    {
        await _ingest.IngestAsync(_connectionId, Allotment(5, 10));
        var stale = await _ingest.IngestAsync(_connectionId, Allotment(3, 99)); // out of order, older

        Assert.Equal(IngestOutcome.DroppedStale, stale.Value!.Outcome);
        Assert.Equal(10, await AllotmentOnAsync(From));           // unchanged by the stale message
    }

    [Fact]
    public async Task Replaying_the_same_message_is_idempotent()
    {
        await _ingest.IngestAsync(_connectionId, Allotment(7, 4));
        var replay = await _ingest.IngestAsync(_connectionId, Allotment(7, 4)); // same seq again

        Assert.Equal(IngestOutcome.DroppedStale, replay.Value!.Outcome);
        var counts = await LogCountsAsync();
        Assert.Equal(1, counts.Applied);                         // applied exactly once
        Assert.Equal(1, counts.Stale);
        Assert.Equal(4, await AllotmentOnAsync(From));
    }

    [Fact]
    public async Task An_unmapped_room_code_quarantines_the_whole_message()
    {
        var result = await _ingest.IngestAsync(_connectionId, Allotment(1, 6, room: "UNKNOWN"));

        Assert.Equal(IngestOutcome.Quarantined, result.Value!.Outcome);
        Assert.Equal((0, 0, 1), await LogCountsAsync());

        // Sequence did not advance — a subsequent valid low seq still applies.
        var next = await _ingest.IngestAsync(_connectionId, Allotment(1, 6));
        Assert.Equal(IngestOutcome.Applied, next.Value!.Outcome);
        Assert.Equal(6, await AllotmentOnAsync(From));
    }

    [Fact]
    public async Task Applying_emits_a_channel_ari_applied_event_to_the_outbox()
    {
        await _ingest.IngestAsync(_connectionId, Allotment(1, 5));

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var type = await conn.ExecuteScalarAsync<string>(
            "SELECT type FROM channel.outbox_message ORDER BY occurred_at LIMIT 1");
        Assert.Equal("stay.channel.ari-applied", type);

        var stale = await _ingest.IngestAsync(_connectionId, Allotment(1, 5)); // replay
        Assert.Equal(IngestOutcome.DroppedStale, stale.Value!.Outcome);
        var outboxCount = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM channel.outbox_message");
        Assert.Equal(1, outboxCount);                            // no event for a dropped message
    }

    [Fact]
    public async Task A_rate_update_on_a_mapped_rate_plan_is_applied()
    {
        var msg = new AriIngestMessage(1,
            [new AriUpdate("DLX", From, ToExclusive, null, 1500.00m, "INR", "BAR")]);

        var result = await _ingest.IngestAsync(_connectionId, msg);

        Assert.Equal(IngestOutcome.Applied, result.Value!.Outcome);
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var price = await conn.ExecuteScalarAsync<decimal>(
            "SELECT base_price FROM ari.rate_calendar WHERE room_type_id = @RoomTypeId AND rate_plan_id = @RatePlanId AND stay_date = @From",
            new { RoomTypeId, RatePlanId, From });
        Assert.Equal(1500.00m, price);
    }
}
