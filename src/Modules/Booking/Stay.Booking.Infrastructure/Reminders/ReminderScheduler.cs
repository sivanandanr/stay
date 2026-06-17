using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Booking.Contracts;

namespace Stay.Booking.Infrastructure.Reminders;

/// <summary>
/// Platform-owned pre-arrival reminder scheduler (§8): for each confirmed booking it computes the
/// lead time to check-in IN THE PROPERTY TIMEZONE (BR-4) and emits any due reminder that hasn't
/// already fired. The <c>booking.reminder_log</c> PK makes each (booking, type) fire at most once
/// (BR-5); the emit + the ledger row commit together (no dual-write, BR-11). The NotificationAdapter
/// delivers off the emitted event.
/// </summary>
public sealed class ReminderScheduler(string connectionString)
{
    static ReminderScheduler() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    // Each reminder fires once the lead time has fallen to its threshold (the log dedupes redelivery).
    private static readonly (string Type, int Hours)[] Schedule = [("T48", 48), ("T24", 24)];

    /// <summary>The reminder types due at <paramref name="leadHours"/> before check-in (pure).</summary>
    public static IEnumerable<string> DueReminderTypes(double leadHours) =>
        leadHours <= 0 ? [] : Schedule.Where(s => leadHours <= s.Hours).Select(s => s.Type);

    /// <summary>Emits any newly-due reminders for confirmed bookings; returns how many were emitted.</summary>
    public async Task<int> ReapDueAsync(DateTimeOffset now, int batchSize = 500, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var candidates = await conn.QueryAsync<Candidate>(new CommandDefinition("""
            SELECT b.id AS BookingId, b.reference AS Reference, br.check_in AS CheckIn,
                   b.cancellation_snapshot AS Snapshot
            FROM booking.booking b
            JOIN booking.booking_room br ON br.booking_id = b.id AND br.status = 'ACTIVE'
            WHERE b.status = 'CONFIRMED' AND b.cancellation_snapshot IS NOT NULL
            ORDER BY br.check_in
            LIMIT @batchSize
            """, new { batchSize }, tx, cancellationToken: ct));

        var emitted = 0;
        foreach (var candidate in candidates)
        {
            var snapshot = JsonSerializer.Deserialize<CancellationSnapshot>(candidate.Snapshot);
            if (snapshot is null)
                continue;

            var leadHours = (CheckInInstant(candidate.CheckIn, snapshot) - now).TotalHours;

            foreach (var type in DueReminderTypes(leadHours))
            {
                var inserted = await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO booking.reminder_log (booking_id, reminder_type)
                    VALUES (@BookingId, @type) ON CONFLICT DO NOTHING
                    """, new { candidate.BookingId, type }, tx, cancellationToken: ct));

                if (inserted == 0)
                    continue; // already fired

                var @event = new PreArrivalReminderDue(
                    Guid.NewGuid(), candidate.BookingId, candidate.Reference, type, now);
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
                    new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));
                emitted++;
            }
        }

        await tx.CommitAsync(ct);
        return emitted;
    }

    private static DateTimeOffset CheckInInstant(DateOnly checkIn, CancellationSnapshot snapshot)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(snapshot.Timezone);
        var local = checkIn.ToDateTime(snapshot.CheckInTime); // wall-clock in the property timezone
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    private sealed record Candidate(long BookingId, string Reference, DateOnly CheckIn, string Snapshot);
}
