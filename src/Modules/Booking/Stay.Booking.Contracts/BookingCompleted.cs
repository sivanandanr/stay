using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>
/// Raised when a stay completes (checkout has passed). Carries the denormalized ids the reviews
/// context needs to mark the stay reviewable — event-carried state, no cross-context read (BR-6).
/// </summary>
public sealed record BookingCompleted(
    Guid EventId, long BookingId, long GuestId, long PropertyId, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.completed";
}
