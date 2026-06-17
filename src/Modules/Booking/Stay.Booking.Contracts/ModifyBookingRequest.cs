using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>Body for <c>POST /api/v1/bookings/{id}/modify</c> — change the stay dates (same room type/quantity).</summary>
public sealed record ModifyBookingRequest(DateOnly CheckIn, DateOnly CheckOut);

/// <summary>The outcome of a modification: the new total and the price delta vs. the prior booking.</summary>
public sealed record ModifyResult(long BookingId, string Reference, decimal NewTotal, decimal Delta);

/// <summary>
/// Raised when a booking is modified (dates changed, inventory moved, re-quoted). The price delta is
/// settled separately (additional charge or partial refund) — the saga doesn't block on it (BR-11).
/// </summary>
public sealed record BookingModified(
    Guid EventId, long BookingId, string Reference, decimal NewTotal, decimal Delta, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.modified";
}
