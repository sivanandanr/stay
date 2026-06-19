using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.BuildingBlocks;
using Stay.Booking.Contracts;
using Stay.Loyalty.Infrastructure;
using Stay.Payment.Contracts;
using Stay.Promotion.Infrastructure;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// Confirms a held booking with payment (CLAUDE.md §9). Flow: authorize via <see cref="IPaymentGateway"/>
/// → on authorization, commit inventory (held → sold) + CONFIRMED + an AUTHORIZED payment row in one
/// transaction (inventory + payment commit synchronously, BR-11/§1.6) → capture. A capture failure
/// after authorization NEVER blocks the guest: the booking stays CONFIRMED and the payment is left
/// AUTHORIZED for the finance retry queue. Idempotent by key <c>stay:{booking_id}:{attempt}</c> (BR-5).
/// </summary>
public sealed class BookingConfirmService(
    string connectionString, IPaymentGateway gateway, PromotionService promotions, LoyaltyService loyalty)
{
    private const string Psp = "RAZORPAY";
    private readonly InventoryRepository _inventory = new();

    /// <summary>
    /// SCAFFOLD (§9, unverified): opens a Razorpay Checkout order for a HELD booking so the mobile client
    /// can complete payment (cards/UPI/netbanking; 3DS handled by Razorpay). The returned <see cref="OrderResult"/>
    /// is checkout params only — never a secret (PCI SAQ-A). The frozen-quote total (BR-2) is the order amount;
    /// the saga still authorizes/commits on <see cref="ConfirmAsync"/> after the client returns. No DB write here —
    /// the order is owned by the PSP until confirm. Real signature verification will live in the confirm path once
    /// the PaymentGateway order contract is wired.
    /// </summary>
    public async Task<Result<OrderResult>> CreatePaymentOrderAsync(
        long bookingId, long requireGuestId, int attempt = 1, CancellationToken ct = default)
    {
        var booking = await ReadBookingAsync(bookingId, ct);
        if (booking is null)
            return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found.");
        if (booking.GuestId != requireGuestId)
            return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found."); // tenancy: don't leak existence
        if (booking.Status == "CONFIRMED")
            return Error.Conflict("already-confirmed", "This booking is already confirmed.");
        if (booking.Status != "HELD")
            return Error.Conflict("invalid-state", $"A {booking.Status} booking cannot be paid for.");
        if (IsExpired(booking))
            return Error.Conflict("hold-expired", "The hold has expired; please start a new booking.");

        var key = $"stay:{bookingId}:{attempt}";
        var order = await gateway.CreateOrderAsync(
            new OrderRequest(key, bookingId, booking.TotalAmount, booking.Currency), ct);
        return Result<OrderResult>.Success(order);
    }

    public async Task<Result<ConfirmResult>> ConfirmAsync(
        long bookingId, int attempt = 1, CheckoutProof? proof = null, CancellationToken ct = default)
    {
        var booking = await ReadBookingAsync(bookingId, ct);
        if (booking is null)
            return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found.");
        if (booking.Status == "CONFIRMED")
            return Result<ConfirmResult>.Success(new ConfirmResult(booking.Id, booking.Reference, booking.Status));
        if (booking.Status != "HELD")
            return Error.Conflict("invalid-state", $"A {booking.Status} booking cannot be confirmed.");
        if (IsExpired(booking))
            return Error.Conflict("hold-expired", "The hold has expired; please start a new booking.");

        // Settle payment BEFORE committing (an external call shouldn't hold the booking row lock). Two
        // paths to the same authorized PSP ref:
        //   • client-driven (proof present): the guest paid via Razorpay Checkout → verify the signature
        //     through PaymentGateway (never compute the HMAC here, §9).
        //   • server-driven (no proof): authorize the frozen total directly (mock / pay-on-confirm).
        var key = $"stay:{bookingId}:{attempt}";
        string pspRef;
        if (proof is not null)
        {
            var verify = await gateway.VerifyCheckoutAsync(proof, key, ct);
            if (!verify.Verified)
                return Error.Conflict("payment-verification-failed",
                    verify.Reason is { } r ? $"Payment could not be verified ({r})." : "Payment could not be verified.");
            pspRef = verify.PspRef!;
        }
        else
        {
            var auth = await gateway.AuthorizeAsync(
                new PaymentInstruction(key, bookingId, booking.TotalAmount, booking.Currency), ct);
            if (!auth.Authorized)
                return Error.Conflict("payment-declined", auth.DeclineReason ?? "Payment was declined.");
            pspRef = auth.PspRef!;
        }

        // Commit inventory + booking + the AUTHORIZED payment row atomically.
        long paymentId;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var locked = await conn.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition("""
                SELECT id AS Id, reference AS Reference, status AS Status, hold_expires_at AS HoldExpiresAt,
                       total_amount AS TotalAmount, currency AS Currency,
                       coupon_code AS CouponCode, room_subtotal AS RoomSubtotal,
                       guest_id AS GuestId, points_redeemed AS PointsRedeemed, loyalty_discount AS LoyaltyDiscount,
                       property_id AS PropertyId
                FROM booking.booking WHERE id = @bookingId FOR UPDATE
                """, new { bookingId }, tx, cancellationToken: ct));

            if (locked is null)
                return Error.NotFound("booking-not-found", $"Booking {bookingId} was not found.");
            if (locked.Status == "CONFIRMED")
                return Result<ConfirmResult>.Success(new ConfirmResult(locked.Id, locked.Reference, locked.Status));
            if (locked.Status != "HELD")
                return Error.Conflict("invalid-state", $"A {locked.Status} booking cannot be confirmed.");
            if (IsExpired(locked))
                return Error.Conflict("hold-expired", "The hold has expired; please start a new booking.");

            var rooms = (await conn.QueryAsync<RoomLine>(new CommandDefinition("""
                SELECT room_type_id AS RoomTypeId, check_in AS CheckIn, check_out AS CheckOut, quantity AS Quantity
                FROM booking.booking_room WHERE booking_id = @bookingId AND status = 'ACTIVE'
                """, new { bookingId }, tx, cancellationToken: ct))).AsList();

            foreach (var room in rooms)
                await _inventory.ConfirmAsync(conn, tx, room.RoomTypeId, room.CheckIn, room.CheckOut, room.Quantity, ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE booking.inventory_hold SET released = true WHERE booking_id = @bookingId",
                new { bookingId }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE booking.booking
                SET status = 'CONFIRMED', hold_expires_at = NULL, updated_at = now(), row_version = row_version + 1
                WHERE id = @bookingId
                """, new { bookingId }, tx, cancellationToken: ct));

            // Redeem the coupon atomically with the inventory/payment commit (user-approved in-saga
            // redemption). The discount was frozen at hold (BR-2); a supply failure (budget/limit)
            // rolls back the whole confirm, so a discount is never given without recording it.
            if (!string.IsNullOrWhiteSpace(locked.CouponCode))
            {
                // The coupon's share of the discount = subtotal − total − the loyalty share (both frozen at hold).
                var discount = locked.RoomSubtotal - locked.TotalAmount - locked.LoyaltyDiscount;
                var redeem = await promotions.RedeemInTransactionAsync(
                    conn, tx, locked.CouponCode, bookingId, guestId: null, discount, DateTimeOffset.UtcNow, ct);
                if (!redeem.IsSuccess)
                    return redeem.Error!.Value; // exits the tx block uncommitted → rollback
            }

            // Redeem loyalty points atomically with the commit (user-approved in-saga redemption). The
            // points discount was frozen at hold (BR-2); if the guest no longer has the points (spent
            // elsewhere since the hold), the whole confirm rolls back. Idempotent per booking.
            if (locked.PointsRedeemed > 0)
            {
                var redeem = await loyalty.RedeemInTransactionAsync(
                    conn, tx, locked.GuestId, locked.PointsRedeemed, $"booking:{bookingId}:loyalty",
                    reason: "Booking redemption", reference: locked.Reference, ct: ct);
                if (!redeem.IsSuccess)
                    return redeem.Error!.Value; // exits the tx block uncommitted → rollback
            }

            paymentId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO payment.payment
                    (booking_id, psp, psp_ref, amount, currency, status, idempotency_key)
                VALUES (@bookingId, @Psp, @pspRef, @amount, @currency, 'AUTHORIZED', @key)
                RETURNING id
                """,
                new { bookingId, Psp, pspRef, amount = locked.TotalAmount, currency = locked.Currency, key },
                tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.status_history (booking_id, from_status, to_status) VALUES (@bookingId, 'HELD', 'CONFIRMED')",
                new { bookingId }, tx, cancellationToken: ct));

            var @event = new BookingConfirmed(
                Guid.NewGuid(), bookingId, locked.Reference, DateTimeOffset.UtcNow, locked.PropertyId);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO booking.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
                new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }

        // Capture AFTER the commit. Failure here must not block the guest — the booking is CONFIRMED.
        var capture = await gateway.CaptureAsync(pspRef, key, ct);
        await MarkCaptureAsync(paymentId, capture.Captured, ct);

        return Result<ConfirmResult>.Success(new ConfirmResult(bookingId, booking.Reference, "CONFIRMED"));
    }

    private static bool IsExpired(BookingRow b) => b.HoldExpiresAt is { } expiry && expiry < DateTime.UtcNow;

    private async Task<BookingRow?> ReadBookingAsync(long bookingId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition("""
            SELECT id AS Id, reference AS Reference, status AS Status, hold_expires_at AS HoldExpiresAt,
                   total_amount AS TotalAmount, currency AS Currency,
                   coupon_code AS CouponCode, room_subtotal AS RoomSubtotal,
                   guest_id AS GuestId, points_redeemed AS PointsRedeemed, loyalty_discount AS LoyaltyDiscount,
                   property_id AS PropertyId
            FROM booking.booking WHERE id = @bookingId
            """, new { bookingId }, cancellationToken: ct));
    }

    private async Task MarkCaptureAsync(long paymentId, bool captured, CancellationToken ct)
    {
        // On capture failure the row stays AUTHORIZED → the finance retry queue picks it up (§9).
        if (!captured)
            return;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE payment.payment SET status = 'CAPTURED', updated_at = now(), row_version = row_version + 1 WHERE id = @paymentId",
            new { paymentId }, cancellationToken: ct));
    }

    private sealed record BookingRow(
        long Id, string Reference, string Status, DateTime? HoldExpiresAt, decimal TotalAmount, string Currency,
        string? CouponCode, decimal RoomSubtotal, long GuestId, int PointsRedeemed, decimal LoyaltyDiscount,
        long PropertyId);
}
