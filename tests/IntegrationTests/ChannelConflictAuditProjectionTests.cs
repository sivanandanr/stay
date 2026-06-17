using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Channel.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Channel conflict resolutions are projected into admin.audit_log, idempotently (§10).</summary>
public sealed class ChannelConflictAuditProjectionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AdminSchema.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private AdminDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AdminDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new AdminDbContext(options);
    }

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent @event) =>
        new(@event.EventId, @event.EventType, JsonSerializer.Serialize(@event, @event.GetType()), DateTimeOffset.UtcNow);

    [Fact]
    public async Task ConflictResolved_is_recorded_with_the_note_and_is_idempotent()
    {
        var envelope = EnvelopeFor(new ChannelConflictResolved(
            Guid.NewGuid(), ConflictId: 12, PropertyId: 55, "OVERBOOK", "ops|3", "RESOLVED",
            "Trimmed channel allotment.", DateTimeOffset.UtcNow));

        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new ChannelConflictAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync(); // exactly one despite 3 deliveries
            Assert.Equal("channel.conflict_resolve", entry.Action);
            Assert.Equal("sync_conflict", entry.EntityType);
            Assert.Equal("12", entry.EntityId);
            Assert.Equal("ops|3", entry.ActorSub);
            Assert.Equal("Trimmed channel allotment.", entry.Reason);
            Assert.Contains("RESOLVED", entry.After!);
        }
    }
}
