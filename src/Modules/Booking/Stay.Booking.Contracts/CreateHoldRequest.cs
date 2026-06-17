namespace Stay.Booking.Contracts;

/// <summary>
/// Body for <c>POST /api/v1/holds</c>. The guest comes from the token; the idempotency key from the
/// <c>Idempotency-Key</c> header. Single room type for now.
/// </summary>
public sealed record CreateHoldRequest(
    long PropertyId,
    long RoomTypeId,
    long RatePlanId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Quantity,
    short Adults,
    short Children,
    CancellationSnapshot? Cancellation = null);

/// <summary>Body for <c>POST /api/v1/bookings/{id}/cancel</c>.</summary>
public sealed record CancelBookingRequest(string Reason);
