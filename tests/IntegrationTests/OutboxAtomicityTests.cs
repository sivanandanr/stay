using Dapper;
using Npgsql;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The outbox write shares the caller's transaction, so the event and the state change commit or
/// roll back together — there is no dual-write window (BR-11).
/// </summary>
public sealed class OutboxAtomicityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(OutboxTestInfra.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Rolled_back_transaction_persists_neither_the_change_nor_the_event()
    {
        var eventId = Guid.NewGuid();
        var code = $"TEST_{eventId:N}";
        var writer = new OutboxWriter();

        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var amenityId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO catalog.amenity (code, category, label) VALUES (@code, 'TEST', 'probe') RETURNING id",
                new { code }, tx));

            await writer.WriteAsync(conn, tx, "catalog",
                new TestEvent(eventId, amenityId, DateTimeOffset.UtcNow));

            await tx.RollbackAsync();
        }

        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM catalog.amenity WHERE code = @code", new { code }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM catalog.outbox_message WHERE id = @eventId", new { eventId }));
        }
    }
}
