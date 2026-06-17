using Dapper;
using Npgsql;
using Stay.Booking.Infrastructure.Holds;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / §10 — an ops manual booking override force-sets a terminal status with a mandatory reason,
/// records the status_history trail, emits an audit event, and is idempotent.
/// </summary>
public sealed class ManualOverrideTests : IAsyncLifetime
{
    private const string Actor = "ops|1";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private ManualOverrideService _overrides = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _overrides = new ManualOverrideService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<long> SeedBookingAsync(string status)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO booking.booking (reference, idempotency_key, guest_id, contact_email, property_id, status, currency)
            VALUES ('BK-' || gen_random_uuid(), gen_random_uuid()::text, 7, 'g@x.io', 55, @status, 'INR')
            RETURNING id
            """, new { status });
    }

    private async Task<(string Status, int History, int Outbox)> StateAsync(long bookingId)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var status = await conn.ExecuteScalarAsync<string>("SELECT status FROM booking.booking WHERE id = @bookingId", new { bookingId });
        var history = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM booking.status_history WHERE booking_id = @bookingId", new { bookingId });
        var outbox = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM booking.outbox_message", new { });
        return (status!, history, outbox);
    }

    [Fact]
    public async Task Overriding_changes_status_records_history_and_emits_an_event()
    {
        var id = await SeedBookingAsync("CONFIRMED");

        var result = await _overrides.AdjustStatusAsync(id, Actor, "CANCELLED", "Goodwill cancellation.");

        Assert.True(result.IsSuccess);
        Assert.Equal("CONFIRMED", result.Value!.FromStatus);
        var state = await StateAsync(id);
        Assert.Equal("CANCELLED", state.Status);
        Assert.Equal(1, state.History);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task A_reason_is_mandatory()
    {
        var id = await SeedBookingAsync("CONFIRMED");

        var result = await _overrides.AdjustStatusAsync(id, Actor, "CANCELLED", "   ");

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }

    [Fact]
    public async Task An_unsupported_target_status_is_rejected()
    {
        var id = await SeedBookingAsync("CONFIRMED");

        var result = await _overrides.AdjustStatusAsync(id, Actor, "HELD", "nope");

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Overriding_an_unknown_booking_is_not_found()
    {
        var result = await _overrides.AdjustStatusAsync(999_999, Actor, "CANCELLED", "x");

        Assert.False(result.IsSuccess);
        Assert.Equal("booking-not-found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Re_applying_the_same_status_is_a_no_op()
    {
        var id = await SeedBookingAsync("CONFIRMED");

        await _overrides.AdjustStatusAsync(id, Actor, "NO_SHOW", "Guest never arrived.");
        await _overrides.AdjustStatusAsync(id, Actor, "NO_SHOW", "Guest never arrived.");

        var state = await StateAsync(id);
        Assert.Equal("NO_SHOW", state.Status);
        Assert.Equal(1, state.History); // second call changed nothing
        Assert.Equal(1, state.Outbox);
    }
}
