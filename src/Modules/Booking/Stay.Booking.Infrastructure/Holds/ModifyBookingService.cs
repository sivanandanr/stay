using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.BuildingBlocks;
using Stay.Booking.Contracts;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// Modifies a confirmed booking's stay dates (same room type + quantity). In one transaction: re-quote
/// the new dates (frozen, BR-2), move inventory (release the old nights, then sell the new nights
/// all-or-none — releasing first so overlapping nights re-sell cleanly, BR-1), update the room +
/// totals, and emit <see cref="BookingModified"/> with the price delta. The delta is settled
/// separately (charge/refund), so the saga never blocks on payment (BR-11). Tenancy-scoped (BR-9).
/// </summary>
public sealed class ModifyBookingService(string connectionString)
{
    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();

    public async Task<Result<ModifyResult>> ModifyAsync(
        long bookingId, DateOnly newCheckIn, DateOnly newCheckOut, long? requireGuestId = null, CancellationToken ct = default)
    {
        if (newCheckOut <= newCheckIn)
            return Error.Validation("Check-out must be after check-in.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition("""
            SELECT id AS Id, reference AS Reference, status AS Status, total_amount AS TotalAmount, guest_id AS GuestId
            FROM booking.booking WHERE id = @bookingId FOR UPDATE
            """, new { bookingId }, tx, cancellationToken: ct));

        if (booking is null || (requireGuestId is { } gid && booking.GuestId != gid))
            return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found.");
        if (booking.Status != "CONFIRMED")
            return Error.Conflict("invalid-state", $"A {booking.Status} booking cannot be modified.");

        var room = await conn.QuerySingleOrDefaultAsync<RoomRow>(new CommandDefinition("""
            SELECT id AS Id, room_type_id AS RoomTypeId, rate_plan_id AS RatePlanId,
                   check_in AS CheckIn, check_out AS CheckOut, quantity AS Quantity,
                   adults AS Adults, children AS Children
            FROM booking.booking_room WHERE booking_id = @bookingId AND status = 'ACTIVE'
            ORDER BY id LIMIT 1
            """, new { bookingId }, tx, cancellationToken: ct));
        if (room is null)
            return Error.Conflict("no-active-room", "The booking has no active room to modify.");

        // Re-quote the new dates (freeze, BR-2).
        var quote = await _rates.QuoteAsync(
            conn, room.RoomTypeId, room.RatePlanId, newCheckIn, newCheckOut, room.Adults + room.Children, tx, ct);
        if (quote is null)
            return Error.Conflict("price-unavailable", "The new dates cannot be priced.");

        // Release the old nights first so overlapping nights are free to re-sell, then sell the new nights.
        await _inventory.ReleaseConfirmedAsync(conn, tx, room.RoomTypeId, room.CheckIn, room.CheckOut, room.Quantity, ct);
        var sold = await _inventory.TrySellAsync(conn, tx, room.RoomTypeId, newCheckIn, newCheckOut, room.Quantity, ct);
        if (sold == HoldOutcome.SoldOut)
            return Error.Conflict("sold-out", "Not enough inventory for the new dates."); // tx rolls back → booking unchanged

        var newSubtotal = quote.Total * room.Quantity;
        var delta = newSubtotal - booking.TotalAmount;

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE booking.booking_room
            SET check_in = @newCheckIn, check_out = @newCheckOut,
                nightly_breakdown = CAST(@nightlyJson AS jsonb), subtotal = @newSubtotal
            WHERE id = @roomId
            """,
            new { newCheckIn, newCheckOut, nightlyJson = JsonSerializer.Serialize(quote.Nights), newSubtotal, roomId = room.Id },
            tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE booking.booking
            SET room_subtotal = @newSubtotal, total_amount = @newSubtotal, updated_at = now(), row_version = row_version + 1
            WHERE id = @bookingId
            """, new { newSubtotal, bookingId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO booking.status_history (booking_id, from_status, to_status, note) VALUES (@bookingId, 'CONFIRMED', 'CONFIRMED', 'modified')",
            new { bookingId }, tx, cancellationToken: ct));

        var @event = new BookingModified(Guid.NewGuid(), bookingId, booking.Reference, newSubtotal, delta, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<ModifyResult>.Success(new ModifyResult(bookingId, booking.Reference, newSubtotal, delta));
    }

    private sealed record BookingRow(long Id, string Reference, string Status, decimal TotalAmount, long GuestId);
    private sealed record RoomRow(
        long Id, long RoomTypeId, long RatePlanId, DateOnly CheckIn, DateOnly CheckOut, int Quantity, short Adults, short Children);
}
