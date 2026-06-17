using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Booking.Contracts;

namespace Stay.Booking.Infrastructure.Trips;

/// <summary>Reads a guest's own bookings for the "my trips" view (tenancy-scoped by guest id, BR-9).</summary>
public sealed class TripsQueryService(string connectionString)
{
    static TripsQueryService() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    public async Task<IReadOnlyList<TripSummary>> GetTripsAsync(
        long guestId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // One row per booking (its earliest stay night), most recent stays first.
        return (await conn.QueryAsync<TripSummary>(new CommandDefinition("""
            SELECT b.id AS BookingId, b.reference AS Reference, b.status AS Status, b.property_id AS PropertyId,
                   r.check_in AS CheckIn, r.check_out AS CheckOut, b.total_amount AS TotalAmount,
                   b.currency AS Currency, b.created_at AS CreatedAt
            FROM booking.booking b
            JOIN LATERAL (
                SELECT check_in, check_out FROM booking.booking_room
                WHERE booking_id = b.id ORDER BY check_in LIMIT 1
            ) r ON true
            WHERE b.guest_id = @guestId
            ORDER BY r.check_in DESC
            OFFSET @offset LIMIT @pageSize
            """, new { guestId, offset = page * pageSize, pageSize }, cancellationToken: ct))).AsList();
    }
}
