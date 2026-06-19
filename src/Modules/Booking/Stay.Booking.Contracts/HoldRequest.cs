namespace Stay.Booking.Contracts;

/// <summary>
/// A request to hold inventory for a single room type over a stay. The guest is authenticated; the
/// <see cref="IdempotencyKey"/> (from the <c>Idempotency-Key</c> header) makes a retry return the
/// original hold (BR-5).
/// </summary>
public sealed record HoldRequest(
    string IdempotencyKey,
    long GuestId,
    string ContactEmail,
    long PropertyId,
    long RoomTypeId,
    long RatePlanId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Quantity,
    short Adults,
    short Children,
    TimeSpan HoldTtl,
    CancellationSnapshot? Cancellation = null,
    string? CouponCode = null,
    int RedeemPoints = 0);

/// <summary>The outcome of a successful (or idempotently-replayed) hold.</summary>
public sealed record HoldResult(
    long BookingId,
    string Reference,
    string Status,
    string Currency,
    decimal TotalAmount,
    DateTime? HoldExpiresAt); // UTC; Dapper reads timestamptz as DateTime
