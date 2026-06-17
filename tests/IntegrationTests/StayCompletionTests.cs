using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Stay completion (Phase 6 prerequisite): confirmed stays go COMPLETED after checkout (property tz), emitting BookingCompleted.</summary>
public sealed class StayCompletionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private StayCompletionService _completion = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _completion = new StayCompletionService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a CONFIRMED booking with a snapshot (tz) and the given check-out date; returns its id.</summary>
    private async Task<long> SeedConfirmedAsync(DateOnly checkOut)
    {
        var snapshot = JsonSerializer.Serialize(new CancellationSnapshot(
            "Asia/Singapore", new TimeOnly(14, 0), IsRefundable: true, Tiers: []));

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO booking.booking
                (reference, idempotency_key, guest_id, contact_email, property_id, status, currency,
                 total_amount, cancellation_snapshot)
            VALUES ('R1', @key, 7, 'g@example.com', 55, 'CONFIRMED', 'SGD', 300, CAST(@snapshot AS jsonb))
            RETURNING id
            """, new { key = Guid.NewGuid().ToString("N"), snapshot });
        await conn.ExecuteAsync("""
            INSERT INTO booking.booking_room
                (booking_id, room_type_id, rate_plan_id, check_in, check_out, quantity, nightly_breakdown, subtotal)
            VALUES (@id, 7, 3, @checkIn, @checkOut, 1, '[]', 300)
            """, new { id, checkIn = checkOut.AddDays(-3), checkOut });
        return id;
    }

    private async Task<string> StatusAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@id", new { id }))!;
    }

    [Fact]
    public async Task A_stay_past_checkout_is_completed_and_emits_an_event()
    {
        var id = await SeedConfirmedAsync(new DateOnly(2030, 6, 12)); // checkout noon SGT = 04:00Z
        var now = new DateTimeOffset(2030, 6, 13, 0, 0, 0, TimeSpan.Zero); // after checkout

        var completed = await _completion.ReapAsync(now);

        Assert.Equal(1, completed);
        Assert.Equal("COMPLETED", await StatusAsync(id));
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM booking.outbox_message WHERE type='stay.booking.completed'"));

        Assert.Equal(0, await _completion.ReapAsync(now)); // idempotent — already completed
    }

    [Fact]
    public async Task A_stay_before_checkout_is_left_confirmed()
    {
        var id = await SeedConfirmedAsync(new DateOnly(2030, 6, 12));
        var now = new DateTimeOffset(2030, 6, 10, 0, 0, 0, TimeSpan.Zero); // still mid-stay

        Assert.Equal(0, await _completion.ReapAsync(now));
        Assert.Equal("CONFIRMED", await StatusAsync(id));
    }
}
