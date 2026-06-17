using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Booking.Contracts;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// Releases expired holds (BR-3): finds HELD bookings whose <c>hold_expires_at</c> has lapsed,
/// returns their inventory (units_held down), marks them EXPIRED, and emits <see cref="BookingExpired"/>.
/// Uses <c>FOR UPDATE SKIP LOCKED</c> so it can run concurrently with confirm and other reaper
/// instances without double-processing. Idempotent by construction (only HELD rows are picked up).
/// </summary>
public sealed class HoldReaper(string connectionString)
{
    private readonly InventoryRepository _inventory = new();

    /// <summary>Reaps up to <paramref name="batchSize"/> expired holds; returns how many were expired.</summary>
    public async Task<int> ReapAsync(int batchSize = 100, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var expired = (await conn.QueryAsync<ExpiredBooking>(new CommandDefinition("""
            SELECT id AS Id, reference AS Reference
            FROM booking.booking
            WHERE status = 'HELD' AND hold_expires_at < now()
            ORDER BY hold_expires_at
            FOR UPDATE SKIP LOCKED
            LIMIT @batchSize
            """, new { batchSize }, tx, cancellationToken: ct))).AsList();

        foreach (var booking in expired)
        {
            var rooms = (await conn.QueryAsync<RoomLine>(new CommandDefinition("""
                SELECT room_type_id AS RoomTypeId, check_in AS CheckIn, check_out AS CheckOut, quantity AS Quantity
                FROM booking.booking_room WHERE booking_id = @Id AND status = 'ACTIVE'
                """, new { booking.Id }, tx, cancellationToken: ct))).AsList();

            foreach (var room in rooms)
                await _inventory.ReleaseAsync(conn, tx, room.RoomTypeId, room.CheckIn, room.CheckOut, room.Quantity, ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE booking.inventory_hold SET released = true WHERE booking_id = @Id",
                new { booking.Id }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE booking.booking
                SET status = 'EXPIRED', hold_expires_at = NULL, updated_at = now(), row_version = row_version + 1
                WHERE id = @Id
                """, new { booking.Id }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.status_history (booking_id, from_status, to_status) VALUES (@Id, 'HELD', 'EXPIRED')",
                new { booking.Id }, tx, cancellationToken: ct));

            var @event = new BookingExpired(Guid.NewGuid(), booking.Id, booking.Reference, DateTimeOffset.UtcNow);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
                new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return expired.Count;
    }

    private sealed record ExpiredBooking(long Id, string Reference);
}
