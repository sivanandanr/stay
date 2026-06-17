using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Reminders;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Pre-arrival reminder scheduling (Phase 5 / §8): timezone-aware, fires once per type.</summary>
public sealed class ReminderSchedulerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private ReminderScheduler _scheduler = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _scheduler = new ReminderScheduler(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // Check-in 14:00 Asia/Singapore on 2030-06-12 = 2030-06-12 06:00Z.
    private static readonly DateOnly CheckIn = new(2030, 6, 12);

    /// <summary>Seeds a CONFIRMED booking with a policy snapshot (for tz + check-in time); returns its id.</summary>
    private async Task<long> SeedConfirmedAsync()
    {
        var snapshot = JsonSerializer.Serialize(new CancellationSnapshot(
            "Asia/Singapore", new TimeOnly(14, 0), IsRefundable: true, Tiers: []));

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO booking.booking
                (reference, idempotency_key, guest_id, contact_email, property_id, status, currency,
                 total_amount, cancellation_snapshot)
            VALUES ('R1', @key, 1, 'g@example.com', 99, 'CONFIRMED', 'SGD', 300, CAST(@snapshot AS jsonb))
            RETURNING id
            """, new { key = Guid.NewGuid().ToString("N"), snapshot });

        await conn.ExecuteAsync("""
            INSERT INTO booking.booking_room
                (booking_id, room_type_id, rate_plan_id, check_in, check_out, quantity, nightly_breakdown, subtotal)
            VALUES (@id, 7, 3, @CheckIn, '2030-06-15', 1, '[]', 300)
            """, new { id, CheckIn });
        return id;
    }

    private async Task<string[]> EmittedTypesAsync(long bookingId)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.QueryAsync<string>(
            "SELECT reminder_type FROM booking.reminder_log WHERE booking_id=@bookingId ORDER BY reminder_type",
            new { bookingId })).ToArray();
    }

    [Theory]
    [InlineData(60, new string[0])]                 // too far out
    [InlineData(47, new[] { "T48" })]               // inside 48h
    [InlineData(23, new[] { "T24", "T48" })]        // inside 24h → both
    [InlineData(-1, new string[0])]                 // after check-in
    public void Due_reminder_types_follow_the_lead_time(double leadHours, string[] expected) =>
        Assert.Equal(expected.OrderBy(x => x), ReminderScheduler.DueReminderTypes(leadHours).OrderBy(x => x));

    [Fact]
    public async Task A_due_reminder_is_emitted_exactly_once()
    {
        var bookingId = await SeedConfirmedAsync();
        var now = new DateTimeOffset(2030, 6, 10, 7, 0, 0, TimeSpan.Zero); // ~47h before 06:00Z check-in

        var first = await _scheduler.ReapDueAsync(now);
        var second = await _scheduler.ReapDueAsync(now); // re-run

        Assert.Equal(1, first);                          // T48 emitted
        Assert.Equal(0, second);                         // deduped — not re-emitted
        Assert.Equal(["T48"], await EmittedTypesAsync(bookingId));
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM booking.outbox_message WHERE type='stay.booking.pre_arrival_reminder_due'"));
    }

    [Fact]
    public async Task Both_reminders_fire_for_a_late_booking_then_stop()
    {
        var bookingId = await SeedConfirmedAsync();
        var now = new DateTimeOffset(2030, 6, 11, 7, 0, 0, TimeSpan.Zero); // ~23h before check-in → both due

        var emitted = await _scheduler.ReapDueAsync(now);

        Assert.Equal(2, emitted);
        Assert.Equal(["T24", "T48"], await EmittedTypesAsync(bookingId));
        Assert.Equal(0, await _scheduler.ReapDueAsync(now)); // nothing new
    }

    [Fact]
    public async Task A_far_off_booking_emits_nothing()
    {
        await SeedConfirmedAsync();
        var now = new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero); // ~11 days out

        Assert.Equal(0, await _scheduler.ReapDueAsync(now));
    }
}
