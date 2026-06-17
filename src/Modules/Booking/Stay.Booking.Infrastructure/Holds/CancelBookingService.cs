using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Cancellation;
using Stay.Ari.Infrastructure.Inventory;
using Stay.BuildingBlocks;
using Stay.Booking.Contracts;
using Stay.Payment.Contracts;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// The booking-time cancellation policy snapshot needed to evaluate a refund: the property's tiers,
/// its IANA timezone, and the room's check-in time (BR-4). When supplied, it overrides any manual
/// refund percentage.
/// </summary>
public sealed record CancellationContext(CancellationPolicy Policy, TimeOnly CheckInTime, string PropertyTimeZone);

/// <summary>
/// Cancels a confirmed booking (CLAUDE.md §9). In one transaction: restore inventory (units_sold
/// down) BEFORE the refund, mark the booking/rooms CANCELLED, snapshot the policy, and record a
/// PENDING refund row. Then issue the refund through <see cref="IPaymentGateway"/>. A refund failure
/// does NOT roll back — the inventory is already restored, so the refund is queued for retry + a
/// finance alert. Idempotent (BR-5).
/// </summary>
public sealed class CancelBookingService(string connectionString, IPaymentGateway gateway)
{
    private readonly InventoryRepository _inventory = new();

    public async Task<Result<CancelResult>> CancelAsync(
        long bookingId, string reason, string initiatedBy, int refundPercent = 100,
        long? requireGuestId = null, CancellationContext? policyContext = null, CancellationToken ct = default)
    {
        refundPercent = Math.Clamp(refundPercent, 0, 100);

        long? refundId = null;
        long? paymentId = null;
        string? capturedPspRef = null;
        decimal refundAmount;
        string reference;
        string currency;
        var refundKey = $"stay:refund:{bookingId}:1";

        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition("""
                SELECT id AS Id, reference AS Reference, status AS Status, total_amount AS TotalAmount,
                       currency AS Currency, guest_id AS GuestId, cancellation_snapshot AS CancellationSnapshot
                FROM booking.booking WHERE id = @bookingId FOR UPDATE
                """, new { bookingId }, tx, cancellationToken: ct));

            // Same response whether it's missing or not the caller's — don't leak existence (BR-9).
            if (booking is null || (requireGuestId is { } gid && booking.GuestId != gid))
                return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found.");
            if (booking.Status == "CANCELLED")
                return Result<CancelResult>.Success(new CancelResult(booking.Id, booking.Reference, booking.Status, 0m));
            if (booking.Status != "CONFIRMED")
                return Error.Conflict("invalid-state", $"A {booking.Status} booking cannot be cancelled here.");

            reference = booking.Reference;
            currency = booking.Currency;

            var rooms = (await conn.QueryAsync<RoomLine>(new CommandDefinition("""
                SELECT room_type_id AS RoomTypeId, check_in AS CheckIn, check_out AS CheckOut, quantity AS Quantity
                FROM booking.booking_room WHERE booking_id = @bookingId AND status = 'ACTIVE'
                """, new { bookingId }, tx, cancellationToken: ct))).AsList();

            // Prefer an explicit override (ops), else the policy frozen on the booking at hold time;
            // evaluate the refund from the tiers in the property timezone (BR-4). With no policy at
            // all, fall back to the supplied manual percentage.
            var context = policyContext ?? FromSnapshot(booking.CancellationSnapshot);
            if (context is not null && rooms.Count > 0)
                refundPercent = CancellationPolicyEvaluator.RefundPercent(
                    context.Policy, rooms.Min(r => r.CheckIn), context.CheckInTime,
                    context.PropertyTimeZone, DateTimeOffset.UtcNow);

            refundAmount = Math.Round(booking.TotalAmount * refundPercent / 100m, 2);

            var payment = await conn.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition("""
                SELECT id AS Id, psp_ref AS PspRef FROM payment.payment
                WHERE booking_id = @bookingId ORDER BY id DESC LIMIT 1
                """, new { bookingId }, tx, cancellationToken: ct));
            paymentId = payment?.Id;
            capturedPspRef = payment?.PspRef;

            // Restore inventory BEFORE the refund (§9).
            foreach (var room in rooms)
                await _inventory.ReleaseConfirmedAsync(conn, tx, room.RoomTypeId, room.CheckIn, room.CheckOut, room.Quantity, ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE booking.booking_room SET status = 'CANCELLED' WHERE booking_id = @bookingId",
                new { bookingId }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE booking.booking
                SET status = 'CANCELLED', updated_at = now(), row_version = row_version + 1
                WHERE id = @bookingId
                """, new { bookingId }, tx, cancellationToken: ct));

            var policySnapshot = JsonSerializer.Serialize(new { refundable = refundPercent > 0, refund_pct = refundPercent });
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO booking.cancellation (booking_id, reason, refund_amount, refund_currency, policy_snapshot, initiated_by)
                VALUES (@bookingId, @reason, @refundAmount, @currency, CAST(@policySnapshot AS jsonb), @initiatedBy)
                """,
                new { bookingId, reason, refundAmount, currency, policySnapshot, initiatedBy }, tx, cancellationToken: ct));

            if (refundAmount > 0 && paymentId is not null)
                refundId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO payment.refund (payment_id, amount, currency, reason, status, idempotency_key)
                    VALUES (@paymentId, @refundAmount, @currency, @reason, 'PENDING', @refundKey)
                    RETURNING id
                    """,
                    new { paymentId, refundAmount, currency, reason, refundKey }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.status_history (booking_id, from_status, to_status) VALUES (@bookingId, 'CONFIRMED', 'CANCELLED')",
                new { bookingId }, tx, cancellationToken: ct));

            var @event = new BookingCancelled(Guid.NewGuid(), bookingId, reference, refundAmount, DateTimeOffset.UtcNow);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
                new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }

        // Issue the refund AFTER inventory is restored. Failure here is non-blocking (queued for retry, §9).
        if (refundId is not null && capturedPspRef is not null)
        {
            var refund = await gateway.RefundAsync(capturedPspRef, refundAmount, refundKey, ct);
            if (refund.Refunded)
                await SettleRefundAsync(refundId.Value, paymentId!.Value, refund.PspRef!, refundPercent, ct);
        }

        return Result<CancelResult>.Success(new CancelResult(bookingId, reference, "CANCELLED", refundAmount));
    }

    private async Task SettleRefundAsync(long refundId, long paymentId, string pspRef, int refundPercent, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE payment.refund SET status = 'SUCCEEDED', psp_ref = @pspRef WHERE id = @refundId",
            new { pspRef, refundId }, tx, cancellationToken: ct));

        var paymentStatus = refundPercent >= 100 ? "REFUNDED" : "PARTIALLY_REFUNDED";
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE payment.payment SET status = @paymentStatus, updated_at = now(), row_version = row_version + 1 WHERE id = @paymentId",
            new { paymentStatus, paymentId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    /// <summary>Rebuilds the cancellation context from the policy frozen onto the booking at hold time.</summary>
    private static CancellationContext? FromSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
            return null;

        var snapshot = JsonSerializer.Deserialize<CancellationSnapshot>(snapshotJson);
        if (snapshot is null)
            return null;

        var tiers = snapshot.Tiers.Select(t => new CancellationTier(t.HoursBeforeCheckin, t.RefundPct)).ToList();
        return new CancellationContext(
            new CancellationPolicy(snapshot.IsRefundable, tiers), snapshot.CheckInTime, snapshot.Timezone);
    }

    private sealed record BookingRow(
        long Id, string Reference, string Status, decimal TotalAmount, string Currency, long GuestId, string? CancellationSnapshot);
    private sealed record PaymentRow(long Id, string? PspRef);
}
