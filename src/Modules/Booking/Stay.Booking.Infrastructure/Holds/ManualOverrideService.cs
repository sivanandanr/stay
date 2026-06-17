using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// Ops manual status override of a booking (CLAUDE.md §10 — "manual booking overrides/adjustments,
/// manual cancellations... reason mandatory"). Force-sets a terminal status with a required reason,
/// records the operational trail in <c>booking.status_history</c>, and emits an audit-evidence event —
/// all in one transaction. Idempotent: re-applying the current status is a no-op (no event). This is an
/// administrative correction; the guest cancel saga (with policy + refund + inventory restore) remains
/// the normal path.
/// </summary>
public sealed class ManualOverrideService(string connectionString)
{
    private static readonly HashSet<string> Allowed = ["CANCELLED", "NO_SHOW", "COMPLETED"];

    public async Task<Result<ManualOverrideResult>> AdjustStatusAsync(
        long bookingId, string actorSub, string toStatus, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("A reason is required for a manual override.");
        if (!Allowed.Contains(toStatus))
            return Error.Validation($"'{toStatus}' is not a permitted manual status ({string.Join(", ", Allowed)}).");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition(
            "SELECT id AS Id, reference AS Reference, status AS Status FROM booking.booking WHERE id = @bookingId FOR UPDATE",
            new { bookingId }, tx, cancellationToken: ct));

        if (booking is null)
            return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found.");
        if (booking.Status == toStatus)
            return Result<ManualOverrideResult>.Success(new ManualOverrideResult(bookingId, booking.Status, toStatus)); // idempotent

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE booking.booking SET status = @toStatus, updated_at = now(), row_version = row_version + 1 WHERE id = @bookingId",
            new { toStatus, bookingId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO booking.status_history (booking_id, from_status, to_status, note)
            VALUES (@bookingId, @from, @toStatus, @reason)
            """, new { bookingId, from = booking.Status, toStatus, reason }, tx, cancellationToken: ct));

        var @event = new BookingOverridden(
            Guid.NewGuid(), bookingId, booking.Reference, actorSub, booking.Status, toStatus, reason, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<ManualOverrideResult>.Success(new ManualOverrideResult(bookingId, booking.Status, toStatus));
    }

    private sealed record BookingRow(long Id, string Reference, string Status);
}
