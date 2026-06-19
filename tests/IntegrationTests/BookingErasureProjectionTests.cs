using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Booking.Infrastructure.Erasure;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Guest.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The booking context reacts to a guest erasure (BR-8) by anonymizing its contact snapshots, while
/// keeping the booking + its financial figures intact (§10). Idempotent under redelivery (BR-5).
/// </summary>
public sealed class BookingErasureProjectionTests : IAsyncLifetime
{
    private const string Tombstone = "erased@stay.invalid";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private BookingErasureProjection _projection = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _projection = new BookingErasureProjection(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<long> SeedBookingAsync(long guestId, string key)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO booking.booking
                (reference, idempotency_key, guest_id, contact_email, contact_phone, property_id, currency, total_amount)
            VALUES (@key, @key, @guestId, 'jane@example.com', '+91999', 99, 'INR', 300)
            RETURNING id
            """, new { guestId, key });
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent @event) =>
        new(@event.EventId, @event.EventType, JsonSerializer.Serialize(@event, @event.GetType()), DateTimeOffset.UtcNow);

    [Fact]
    public async Task GuestErased_anonymizes_contact_snapshots_but_keeps_the_money()
    {
        const long guestId = 7;
        var bookingId = await SeedBookingAsync(guestId, "BK-1");
        var other = await SeedBookingAsync(guestId: 8, "BK-2"); // a different guest, untouched

        var envelope = EnvelopeFor(new GuestErased(Guid.NewGuid(), guestId, "user|7", 0, 0, DateTimeOffset.UtcNow));
        await _projection.ProjectAsync(envelope);

        Assert.Equal(Tombstone, await ScalarAsync<string>(
            "SELECT contact_email FROM booking.booking WHERE id = @bookingId", new { bookingId }));
        Assert.Null(await ScalarAsync<string?>(
            "SELECT contact_phone FROM booking.booking WHERE id = @bookingId", new { bookingId }));
        Assert.Equal(300m, await ScalarAsync<decimal>(
            "SELECT total_amount FROM booking.booking WHERE id = @bookingId", new { bookingId })); // money kept
        Assert.Equal("jane@example.com", await ScalarAsync<string>(
            "SELECT contact_email FROM booking.booking WHERE id = @other", new { other })); // other guest untouched
    }

    [Fact]
    public async Task Redelivery_is_idempotent()
    {
        const long guestId = 7;
        var bookingId = await SeedBookingAsync(guestId, "BK-1");
        var envelope = EnvelopeFor(new GuestErased(Guid.NewGuid(), guestId, "user|7", 0, 0, DateTimeOffset.UtcNow));

        await _projection.ProjectAsync(envelope);
        await _projection.ProjectAsync(envelope); // replay

        Assert.Equal(Tombstone, await ScalarAsync<string>(
            "SELECT contact_email FROM booking.booking WHERE id = @bookingId", new { bookingId }));
    }
}
