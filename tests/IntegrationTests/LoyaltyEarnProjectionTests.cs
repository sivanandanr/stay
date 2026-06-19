using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Loyalty.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 9 — a completed stay earns loyalty points (1 per 10 units spent), idempotently per booking
/// (an at-least-once redelivery credits once), via event-carried state on BookingCompleted.
/// </summary>
public sealed class LoyaltyEarnProjectionTests : IAsyncLifetime
{
    private const long GuestId = 7;

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private LoyaltyService _loyalty = null!;
    private LoyaltyEarnProjection _projection = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(LoyaltySchema.Ddl);
        _loyalty = new LoyaltyService(_postgres.GetConnectionString());
        _projection = new LoyaltyEarnProjection(_loyalty);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent e) =>
        new(e.EventId, e.EventType, JsonSerializer.Serialize(e, e.GetType()), DateTimeOffset.UtcNow);

    private static BookingCompleted Completed(long bookingId, decimal total) =>
        new(Guid.NewGuid(), bookingId, GuestId, PropertyId: 55, DateTimeOffset.UtcNow, total, "INR");

    [Fact]
    public async Task A_completed_stay_credits_points_for_the_spend()
    {
        await _projection.ProjectAsync(EnvelopeFor(Completed(100, 1500m)));

        Assert.Equal(150, (await _loyalty.GetAsync(GuestId)).Balance); // 1500 / 10
    }

    [Fact]
    public async Task Earning_is_idempotent_per_booking()
    {
        var envelope = EnvelopeFor(Completed(100, 1500m));

        await _projection.ProjectAsync(envelope);
        await _projection.ProjectAsync(envelope); // redelivery

        Assert.Equal(150, (await _loyalty.GetAsync(GuestId)).Balance); // credited once
    }

    [Fact]
    public async Task A_zero_value_stay_earns_nothing()
    {
        await _projection.ProjectAsync(EnvelopeFor(Completed(100, 0m)));

        Assert.Equal(0, (await _loyalty.GetAsync(GuestId)).Balance);
    }

    [Fact]
    public void Points_are_one_per_ten_units_floored()
    {
        Assert.Equal(150, LoyaltyEarnProjection.PointsFor(1500m));
        Assert.Equal(9, LoyaltyEarnProjection.PointsFor(95m));
        Assert.Equal(0, LoyaltyEarnProjection.PointsFor(5m));
    }
}
