using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Reviews.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Review moderation decisions (publish/reject) are projected into admin.audit_log, idempotently (§10).</summary>
public sealed class ReviewModerationAuditProjectionTests : IAsyncLifetime
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
    public async Task ReviewPublished_is_recorded()
    {
        var envelope = EnvelopeFor(new ReviewPublished(Guid.NewGuid(), ReviewId: 42, PropertyId: 55, "admin|7", DateTimeOffset.UtcNow));

        await using (var db = NewDbContext())
            await new ReviewModerationAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync();
            Assert.Equal("review.publish", entry.Action);
            Assert.Equal("review", entry.EntityType);
            Assert.Equal("42", entry.EntityId);
            Assert.Equal("admin|7", entry.ActorSub);
            Assert.Contains("PUBLISHED", entry.After!);
        }
    }

    [Fact]
    public async Task ReviewRejected_records_the_reason_and_is_idempotent()
    {
        var envelope = EnvelopeFor(new ReviewRejected(
            Guid.NewGuid(), 42, 55, "admin|7", "Abusive language.", DateTimeOffset.UtcNow));

        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new ReviewModerationAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync(); // exactly one despite 3 deliveries
            Assert.Equal("review.reject", entry.Action);
            Assert.Equal("Abusive language.", entry.Reason);
        }
    }
}
