using Dapper;
using Npgsql;
using Stay.Channel.Contracts;
using Stay.Channel.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 7 / §10 — resolving a sync conflict is a privileged, audited action: OPEN → RESOLVED/ESCALATED
/// with a mandatory note, idempotent, emitting an audit-evidence event in the same transaction.
/// </summary>
public sealed class ChannelConflictResolutionTests : IAsyncLifetime
{
    private const long PropertyId = 55;

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private ConflictResolutionService _service = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(ChannelSchema.Ddl);
        _service = new ConflictResolutionService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<long> OpenConflictAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO channel.sync_conflict (property_id, type, detail)
            VALUES (@PropertyId, 'OVERBOOK', '{}'::jsonb) RETURNING id
            """, new { PropertyId });
    }

    private async Task<(string Status, string? Resolution, int OutboxCount)> StateAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync<(string Status, string? Resolution)>(
            "SELECT status AS Status, resolution AS Resolution FROM channel.sync_conflict WHERE id = @id", new { id });
        var outbox = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM channel.outbox_message");
        return (row.Status, row.Resolution, outbox);
    }

    [Fact]
    public async Task Resolving_an_open_conflict_closes_it_and_emits_an_audit_event()
    {
        var id = await OpenConflictAsync();

        var result = await _service.ResolveAsync(id, "ops|1", "Reduced channel allotment to match.", escalate: false);

        Assert.Equal("RESOLVED", result.Value!.Status);
        var state = await StateAsync(id);
        Assert.Equal("RESOLVED", state.Status);
        Assert.Equal("Reduced channel allotment to match.", state.Resolution);
        Assert.Equal(1, state.OutboxCount);
    }

    [Fact]
    public async Task Escalating_marks_the_conflict_escalated()
    {
        var id = await OpenConflictAsync();

        var result = await _service.ResolveAsync(id, "ops|1", "Needs manual channel intervention.", escalate: true);

        Assert.Equal("ESCALATED", result.Value!.Status);
        Assert.Equal("ESCALATED", (await StateAsync(id)).Status);
    }

    [Fact]
    public async Task Resolving_is_idempotent_and_does_not_emit_twice()
    {
        var id = await OpenConflictAsync();

        await _service.ResolveAsync(id, "ops|1", "First resolution.", escalate: false);
        var second = await _service.ResolveAsync(id, "ops|2", "Second attempt.", escalate: false);

        Assert.True(second.IsSuccess);
        Assert.Equal("RESOLVED", second.Value!.Status);
        var state = await StateAsync(id);
        Assert.Equal("First resolution.", state.Resolution); // unchanged
        Assert.Equal(1, state.OutboxCount);                  // exactly one event
    }

    [Fact]
    public async Task A_resolution_note_is_required()
    {
        var id = await OpenConflictAsync();

        var result = await _service.ResolveAsync(id, "ops|1", "   ", escalate: false);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Resolving_an_unknown_conflict_is_not_found()
    {
        var result = await _service.ResolveAsync(999_999, "ops|1", "n/a", escalate: false);

        Assert.False(result.IsSuccess);
        Assert.Equal("conflict-not-found", result.Error!.Value.Code);
    }
}
