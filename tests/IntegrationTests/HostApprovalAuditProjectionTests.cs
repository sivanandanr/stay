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

/// <summary>
/// The cross-context audit projection: host approval/rejection events (emitted by Catalog) are
/// projected into admin.audit_log, idempotently (BR-5, CLAUDE.md §10).
/// </summary>
public sealed class HostApprovalAuditProjectionTests : IAsyncLifetime
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
        new(@event.EventId, @event.EventType, JsonSerializer.Serialize(@event, @event.GetType()),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task HostApproved_is_recorded_in_the_audit_log()
    {
        var envelope = EnvelopeFor(new HostApproved(
            Guid.NewGuid(), HostId: 42, ActorSub: "admin|7", PreviousStatus: "PENDING", DateTimeOffset.UtcNow));

        await using (var db = NewDbContext())
            await new HostApprovalAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync();
            Assert.Equal("admin|7", entry.ActorSub);
            Assert.Equal("host.approve", entry.Action);
            Assert.Equal("host", entry.EntityType);
            Assert.Equal("42", entry.EntityId);
            Assert.Contains("PENDING", entry.Before!);
            Assert.Contains("ACTIVE", entry.After!);
            Assert.Null(entry.Reason);
        }
    }

    [Fact]
    public async Task HostRejected_records_the_reason()
    {
        var envelope = EnvelopeFor(new HostRejected(
            Guid.NewGuid(), HostId: 9, ActorSub: "admin|7", PreviousStatus: "PENDING",
            Reason: "Failed KYC.", DateTimeOffset.UtcNow));

        await using (var db = NewDbContext())
            await new HostApprovalAuditProjection(db).ProjectAsync(envelope, default);

        await using (var db = NewDbContext())
        {
            var entry = await db.AuditLog.SingleAsync();
            Assert.Equal("host.reject", entry.Action);
            Assert.Equal("Failed KYC.", entry.Reason);
            Assert.Contains("SUSPENDED", entry.After!);
        }
    }

    [Fact]
    public async Task Redelivery_of_the_same_event_records_exactly_one_row()
    {
        var envelope = EnvelopeFor(new HostApproved(
            Guid.NewGuid(), 42, "admin|7", "PENDING", DateTimeOffset.UtcNow));

        // Project the same event id three times (simulating at-least-once redelivery).
        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new HostApprovalAuditProjection(db).ProjectAsync(envelope, default);

        await using (var verify = NewDbContext())
            Assert.Equal(1, await verify.AuditLog.CountAsync());
    }

    [Fact]
    public async Task Unrelated_event_types_are_ignored()
    {
        var envelope = new OutboxEnvelope(Guid.NewGuid(), "stay.catalog.property-created", "{}", DateTimeOffset.UtcNow);

        await using (var db = NewDbContext())
            await new HostApprovalAuditProjection(db).ProjectAsync(envelope, default);

        await using (var verify = NewDbContext())
        {
            Assert.Equal(0, await verify.AuditLog.CountAsync());
            Assert.Equal(0, await verify.ProcessedEvents.CountAsync());
        }
    }
}
