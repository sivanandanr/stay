using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>
/// Raised when a booking is held. Written to the booking outbox in the same transaction as the hold;
/// drives the pre-confirm guest notification (the saga never blocks on it, BR-11).
/// </summary>
public sealed record BookingHeld(Guid EventId, long BookingId, long GuestId, string Reference, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.held";
}
