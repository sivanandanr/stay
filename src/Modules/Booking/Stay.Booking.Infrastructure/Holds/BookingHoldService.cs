using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.BuildingBlocks;
using Stay.Booking.Contracts;
using Stay.Loyalty.Infrastructure;
using Stay.Promotion.Infrastructure;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>
/// The booking hold saga (Gate G1). In ONE database transaction across Booking + ARI (Golden Rule
/// §1.7): freeze the price (BR-2), atomically hold inventory (BR-1), and persist a HELD booking with
/// its frozen nightly breakdown, the per-night hold rows for the reaper (BR-3), a status-history row,
/// and a BookingHeld outbox event (BR-11). Idempotent by key (BR-5).
/// </summary>
public sealed class BookingHoldService(string connectionString, PromotionService promotions, LoyaltyService loyalty)
{
    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();

    public async Task<Result<HoldResult>> HoldAsync(HoldRequest request, CancellationToken ct = default)
    {
        if (request.CheckOut <= request.CheckIn)
            return Error.Validation("Check-out must be after check-in.");
        if (request.Quantity <= 0)
            return Error.Validation("Quantity must be positive.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. Idempotent replay: a prior hold with this key wins.
        var existing = await ReadByKeyAsync(conn, request.IdempotencyKey, tx, ct);
        if (existing is not null)
        {
            await tx.RollbackAsync(ct);
            return Result<HoldResult>.Success(existing);
        }

        // 2. Freeze the price (BR-2).
        var occupancy = request.Adults + request.Children;
        var quote = await _rates.QuoteAsync(
            conn, request.RoomTypeId, request.RatePlanId, request.CheckIn, request.CheckOut, occupancy, tx, ct);
        if (quote is null)
            return await FailOrReplayAsync(conn, tx, request.IdempotencyKey,
                Error.Conflict("price-unavailable", "The stay cannot be priced for the requested dates."), ct);

        // 3. Atomically hold inventory (BR-1).
        var outcome = await _inventory.TryHoldAsync(
            conn, tx, request.RoomTypeId, request.CheckIn, request.CheckOut, request.Quantity, ct);
        if (outcome == HoldOutcome.SoldOut)
            return await FailOrReplayAsync(conn, tx, request.IdempotencyKey,
                Error.Conflict("sold-out", "Not enough inventory for the requested dates."), ct);

        // 3b. Apply a coupon to the FROZEN price (BR-2). The discount is a read-only preview computed
        //     here and baked into total_amount; the redemption itself commits atomically at confirm.
        var subtotal = quote.Total * request.Quantity;
        decimal discount = 0;
        string? couponCode = null;
        if (!string.IsNullOrWhiteSpace(request.CouponCode))
        {
            var apply = await promotions.ApplyAsync(request.CouponCode, subtotal, quote.Currency, DateTimeOffset.UtcNow, ct);
            if (!apply.IsSuccess)
                return await FailOrReplayAsync(conn, tx, request.IdempotencyKey, apply.Error!.Value, ct);
            discount = apply.Value!.DiscountAmount;
            couponCode = request.CouponCode;
        }

        // 3c. Apply loyalty points to the FROZEN price (BR-2), after the coupon. Preview-only here: we
        //     check the guest can afford the points now and freeze the discount; the points themselves
        //     are decremented atomically at confirm. Cap the points so the discount never exceeds the
        //     remaining bill (so we never redeem points the guest gets no value for, and total >= 0).
        decimal loyaltyDiscount = 0;
        int pointsRedeemed = 0;
        if (request.RedeemPoints > 0)
        {
            var account = await loyalty.GetAsync(request.GuestId, ct);
            if (account.Balance < request.RedeemPoints)
                return await FailOrReplayAsync(conn, tx, request.IdempotencyKey,
                    Error.Conflict("insufficient-points",
                        $"Balance {account.Balance} is too low to redeem {request.RedeemPoints} points."), ct);

            var afterCoupon = subtotal - discount;
            var maxRedeemable = (int)decimal.Floor(afterCoupon / LoyaltyService.PointValue);
            pointsRedeemed = Math.Min(request.RedeemPoints, maxRedeemable);
            loyaltyDiscount = LoyaltyService.DiscountFor(pointsRedeemed);
        }

        var total = subtotal - discount - loyaltyDiscount;

        // 4. Persist the held booking (everything below commits atomically with the hold above).
        var expiresAt = DateTimeOffset.UtcNow.Add(request.HoldTtl);
        var reference = $"STAY-{Guid.NewGuid():N}"[..15].ToUpperInvariant();

        long bookingId;
        try
        {
            var cancellationJson = request.Cancellation is null
                ? null
                : JsonSerializer.Serialize(request.Cancellation);

            bookingId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO booking.booking
                    (reference, idempotency_key, guest_id, contact_email, property_id, status,
                     currency, room_subtotal, total_amount, hold_expires_at, cancellation_snapshot, coupon_code,
                     points_redeemed, loyalty_discount)
                VALUES
                    (@reference, @IdempotencyKey, @GuestId, @ContactEmail, @PropertyId, 'HELD',
                     @currency, @subtotal, @total, @expiresAt, CAST(@cancellationJson AS jsonb), @couponCode,
                     @pointsRedeemed, @loyaltyDiscount)
                RETURNING id
                """,
                new { reference, request.IdempotencyKey, request.GuestId, request.ContactEmail,
                      request.PropertyId, currency = quote.Currency, subtotal, total, expiresAt, cancellationJson, couponCode,
                      pointsRedeemed, loyaltyDiscount },
                tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost a concurrent race on the idempotency key → release our hold and return the winner.
            await tx.RollbackAsync(ct);
            var winner = await ReadByKeyAsync(conn, request.IdempotencyKey, transaction: null, ct);
            return winner is not null
                ? Result<HoldResult>.Success(winner)
                : throw new InvalidOperationException("Hold insert conflicted but no winning booking was found.", ex);
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO booking.booking_room
                (booking_id, room_type_id, rate_plan_id, check_in, check_out, quantity, adults, children,
                 nightly_breakdown, subtotal)
            VALUES
                (@bookingId, @RoomTypeId, @RatePlanId, @CheckIn, @CheckOut, @Quantity, @Adults, @Children,
                 CAST(@nightlyJson AS jsonb), @subtotal)
            """,
            new { bookingId, request.RoomTypeId, request.RatePlanId, request.CheckIn, request.CheckOut,
                  request.Quantity, request.Adults, request.Children,
                  nightlyJson = JsonSerializer.Serialize(quote.Nights), subtotal },
            tx, cancellationToken: ct));

        // Per-night hold rows: the reaper releases these on expiry (BR-3).
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO booking.inventory_hold (booking_id, room_type_id, stay_date, quantity, expires_at)
            SELECT @bookingId, @RoomTypeId, gs::date, @Quantity, @expiresAt
            FROM generate_series(@CheckIn::date, @CheckOut::date - 1, interval '1 day') AS gs
            """,
            new { bookingId, request.RoomTypeId, request.Quantity, expiresAt, request.CheckIn, request.CheckOut },
            tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO booking.status_history (booking_id, from_status, to_status) VALUES (@bookingId, NULL, 'HELD')",
            new { bookingId }, tx, cancellationToken: ct));

        var @event = new BookingHeld(Guid.NewGuid(), bookingId, request.GuestId, reference, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO booking.outbox_message (type, payload)
            VALUES (@type, CAST(@payload AS jsonb))
            """,
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        return Result<HoldResult>.Success(
            new HoldResult(bookingId, reference, "HELD", quote.Currency, total, expiresAt.UtcDateTime));
    }

    /// <summary>On a hold/price failure, roll back, then return the winner if a concurrent same-key hold committed.</summary>
    private async Task<Result<HoldResult>> FailOrReplayAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, Error failure, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        var winner = await ReadByKeyAsync(conn, key, transaction: null, ct);
        return winner is not null ? Result<HoldResult>.Success(winner) : Result<HoldResult>.Failure(failure);
    }

    private static async Task<HoldResult?> ReadByKeyAsync(
        NpgsqlConnection conn, string key, NpgsqlTransaction? transaction, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<HoldResult>(new CommandDefinition("""
            SELECT id AS BookingId, reference AS Reference, status AS Status, currency AS Currency,
                   total_amount AS TotalAmount, hold_expires_at AS HoldExpiresAt
            FROM booking.booking
            WHERE idempotency_key = @key
            """, new { key }, transaction, cancellationToken: ct));
}
