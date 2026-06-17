using Dapper;
using Npgsql;
using Stay.Booking.Infrastructure.Trips;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>The guest "my trips" read: a guest sees only their own bookings, newest stay first (BR-9).</summary>
public sealed class TripsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private TripsQueryService _trips = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _trips = new TripsQueryService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a booking (with one room) for a guest and returns its id.</summary>
    private async Task<long> SeedBookingAsync(long guestId, string status, DateOnly checkIn)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO booking.booking
                (reference, idempotency_key, guest_id, contact_email, property_id, status, currency, total_amount)
            VALUES (@reference, @key, @guestId, 'g@example.com', 55, @status, 'SGD', 300)
            RETURNING id
            """, new { reference = $"R-{Guid.NewGuid():N}"[..10], key = Guid.NewGuid().ToString("N"), guestId, status });
        await conn.ExecuteAsync("""
            INSERT INTO booking.booking_room
                (booking_id, room_type_id, rate_plan_id, check_in, check_out, quantity, nightly_breakdown, subtotal)
            VALUES (@id, 7, 3, @checkIn, @checkOut, 1, '[]', 300)
            """, new { id, checkIn, checkOut = checkIn.AddDays(2) });
        return id;
    }

    [Fact]
    public async Task A_guest_sees_only_their_own_trips_newest_stay_first()
    {
        var earlier = await SeedBookingAsync(guestId: 1, "CONFIRMED", new DateOnly(2030, 6, 10));
        var later = await SeedBookingAsync(guestId: 1, "HELD", new DateOnly(2030, 8, 1));
        await SeedBookingAsync(guestId: 2, "CONFIRMED", new DateOnly(2030, 7, 1)); // another guest

        var trips = await _trips.GetTripsAsync(guestId: 1, page: 0, pageSize: 20);

        Assert.Equal(2, trips.Count);                      // only guest 1's
        Assert.Equal(later, trips[0].BookingId);           // newest stay first
        Assert.Equal(earlier, trips[1].BookingId);
        Assert.All(trips, t => Assert.Equal(55, t.PropertyId));
    }

    [Fact]
    public async Task A_guest_with_no_bookings_gets_an_empty_list()
    {
        await SeedBookingAsync(guestId: 2, "CONFIRMED", new DateOnly(2030, 6, 10));

        Assert.Empty(await _trips.GetTripsAsync(guestId: 1, page: 0, pageSize: 20));
    }

    [Fact]
    public async Task Trips_are_paginated()
    {
        for (var i = 0; i < 3; i++)
            await SeedBookingAsync(guestId: 1, "CONFIRMED", new DateOnly(2030, 6, 10).AddDays(i * 7));

        var page1 = await _trips.GetTripsAsync(1, page: 0, pageSize: 2);
        var page2 = await _trips.GetTripsAsync(1, page: 1, pageSize: 2);

        Assert.Equal(2, page1.Count);
        Assert.Single(page2);
    }
}
