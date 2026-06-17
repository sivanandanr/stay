using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Manual booking overrides are projected into admin.audit_log, idempotently (§10).</summary>
public sealed class BookingOverrideAuditProjectionTests : IAsyncLifetime
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
    public async Task BookingOverridden_is_recorded_with_the_reason_and_is_idempotent()
    {
        var envelope = EnvelopeFor(new BookingOverridden(
            Guid.NewGuid(), BookingId: 88, "BK-1", "ops|7", "CONFIRMED", "CANCELLED",
            "Goodwill cancellation.", DateTimeOffset.UtcNow));

        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new BookingOverrideAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync();
            Assert.Equal("booking.override", entry.Action);
            Assert.Equal("booking", entry.EntityType);
            Assert.Equal("88", entry.EntityId);
            Assert.Equal("ops|7", entry.ActorSub);
            Assert.Equal("Goodwill cancellation.", entry.Reason);
            Assert.Contains("CANCELLED", entry.After!);
        }
    }
}
