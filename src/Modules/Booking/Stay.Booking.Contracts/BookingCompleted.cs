using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>
/// Raised when a stay completes (checkout has passed). Carries the denormalized ids the reviews
/// context needs to mark the stay reviewable, plus the spend the loyalty context earns points on —
/// event-carried state, no cross-context read (BR-6). <see cref="TotalAmount"/>/<see cref="Currency"/>
/// are trailing-optional so older payloads still deserialize.
/// </summary>
public sealed record BookingCompleted(
    Guid EventId, long BookingId, long GuestId, long PropertyId, DateTimeOffset OccurredAt,
    decimal TotalAmount = 0, string Currency = "INR")
    : IIntegrationEvent
{
    public string EventType => "stay.booking.completed";
}
