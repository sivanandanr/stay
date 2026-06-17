using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Property moderation decisions (publish/reject) are projected into admin.audit_log, idempotently.</summary>
public sealed class PropertyModerationAuditProjectionTests : IAsyncLifetime
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
    public async Task PropertyPublished_is_recorded()
    {
        var envelope = EnvelopeFor(new PropertyPublished(Guid.NewGuid(), PropertyId: 100, "admin|7", DateTimeOffset.UtcNow));

        await using (var db = NewDbContext())
            await new PropertyModerationAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync();
            Assert.Equal("property.publish", entry.Action);
            Assert.Equal("property", entry.EntityType);
            Assert.Equal("100", entry.EntityId);
            Assert.Contains("LIVE", entry.After!);
        }
    }

    [Fact]
    public async Task PropertyRejected_records_the_reason_and_is_idempotent()
    {
        var envelope = EnvelopeFor(new PropertyRejected(
            Guid.NewGuid(), 100, "admin|7", "Low-res photos.", DateTimeOffset.UtcNow));

        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new PropertyModerationAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync(); // exactly one despite 3 deliveries
            Assert.Equal("property.reject", entry.Action);
            Assert.Equal("Low-res photos.", entry.Reason);
        }
    }
}
