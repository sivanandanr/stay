using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Guest.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>A data-subject erasure is projected into admin.audit_log, idempotently (§10 — PII erasure).</summary>
public sealed class GuestErasureAuditProjectionTests : IAsyncLifetime
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

    private AdminDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent @event) =>
        new(@event.EventId, @event.EventType, JsonSerializer.Serialize(@event, @event.GetType()), DateTimeOffset.UtcNow);

    [Fact]
    public async Task GuestErased_is_recorded_and_is_idempotent()
    {
        var envelope = EnvelopeFor(new GuestErased(
            Guid.NewGuid(), GuestId: 55, ActorSub: "user|55", TravelersDeleted: 2, PaymentTokensDeleted: 1,
            DateTimeOffset.UtcNow));

        for (var i = 0; i < 3; i++)
            await using (var db = NewDbContext())
                await new GuestErasureAuditProjection(db).ProjectAsync(envelope, default);

        await using var read = NewDbContext();
        var entry = await read.AuditLog.SingleAsync(); // one row despite 3 deliveries
        Assert.Equal("guest.erase", entry.Action);
        Assert.Equal("guest", entry.EntityType);
        Assert.Equal("55", entry.EntityId);
        Assert.Equal("user|55", entry.ActorSub);
        Assert.Null(entry.Before);                          // no PII retained
        Assert.Contains("payment_tokens_deleted", entry.After!);
        Assert.Contains("BR-8", entry.Reason!);
    }
}
