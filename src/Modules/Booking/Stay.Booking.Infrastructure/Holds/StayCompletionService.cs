using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Booking.Contracts;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// Marks confirmed stays COMPLETED once checkout has passed in the property timezone (BR-4), emitting
/// <see cref="BookingCompleted"/> so the reviews context can open a verified review (BR-6). Idempotent:
/// the status guard means each booking transitions once; the emit commits with the transition (BR-11).
/// </summary>
public sealed class StayCompletionService(string connectionString)
{
    static StayCompletionService() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    // Treat the stay as over by midday on the check-out date in the property timezone.
    private static readonly TimeOnly AssumedCheckout = new(12, 0);

    public async Task<int> ReapAsync(DateTimeOffset now, int batchSize = 500, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var candidates = await conn.QueryAsync<Candidate>(new CommandDefinition("""
            SELECT b.id AS BookingId, b.guest_id AS GuestId, b.property_id AS PropertyId,
                   br.check_out AS CheckOut, b.cancellation_snapshot AS Snapshot
            FROM booking.booking b
            JOIN booking.booking_room br ON br.booking_id = b.id AND br.status = 'ACTIVE'
            WHERE b.status = 'CONFIRMED' AND b.cancellation_snapshot IS NOT NULL
            ORDER BY br.check_out
            LIMIT @batchSize
            """, new { batchSize }, tx, cancellationToken: ct));

        var completed = 0;
        foreach (var c in candidates)
        {
            var snapshot = JsonSerializer.Deserialize<CancellationSnapshot>(c.Snapshot);
            if (snapshot is null || now < CheckoutInstant(c.CheckOut, snapshot.Timezone))
                continue;

            var updated = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE booking.booking
                SET status = 'COMPLETED', updated_at = now(), row_version = row_version + 1
                WHERE id = @BookingId AND status = 'CONFIRMED'
                """, new { c.BookingId }, tx, cancellationToken: ct));
            if (updated == 0)
                continue;

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.status_history (booking_id, from_status, to_status) VALUES (@BookingId, 'CONFIRMED', 'COMPLETED')",
                new { c.BookingId }, tx, cancellationToken: ct));

            var @event = new BookingCompleted(Guid.NewGuid(), c.BookingId, c.GuestId, c.PropertyId, now);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
                new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));
            completed++;
        }

        await tx.CommitAsync(ct);
        return completed;
    }

    private static DateTimeOffset CheckoutInstant(DateOnly checkOut, string timezone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var local = checkOut.ToDateTime(AssumedCheckout);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private sealed record Candidate(long BookingId, long GuestId, long PropertyId, DateOnly CheckOut, string Snapshot);
}
