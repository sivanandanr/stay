using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Payment.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Payout runs are projected into admin.audit_log, idempotently (§10 — "payout runs").</summary>
public sealed class PayoutAuditProjectionTests : IAsyncLifetime
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
    public async Task PayoutCompleted_is_recorded_and_is_idempotent()
    {
        var envelope = EnvelopeFor(new PayoutCompleted(
            Guid.NewGuid(), PayoutId: 77, HostId: 4242, NetAmount: 1275.00m, "INR", "PAID",
            "finance|1", DateTimeOffset.UtcNow));

        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new PayoutAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync(); // exactly one despite 3 deliveries
            Assert.Equal("payout.run", entry.Action);
            Assert.Equal("payout", entry.EntityType);
            Assert.Equal("77", entry.EntityId);
            Assert.Equal("finance|1", entry.ActorSub);
            Assert.Contains("PAID", entry.After!);
            Assert.Contains("4242", entry.Reason!);
        }
    }
}
