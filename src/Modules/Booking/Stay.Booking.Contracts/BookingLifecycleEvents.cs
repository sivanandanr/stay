using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>
/// Raised when a held booking is confirmed (inventory committed: held → sold). Drives the confirmation
/// notification and the search popularity (trending) signal. <see cref="PropertyId"/> is trailing-
/// optional so existing call sites/consumers are unaffected (event-carried state for trending).
/// </summary>
public sealed record BookingConfirmed(
    Guid EventId, long BookingId, string Reference, DateTimeOffset OccurredAt, long PropertyId = 0)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.confirmed";
}

/// <summary>Raised when the reaper expires a held booking whose hold lapsed (inventory released, BR-3).</summary>
public sealed record BookingExpired(Guid EventId, long BookingId, string Reference, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.expired";
}

/// <summary>Raised when a booking is cancelled (inventory restored; a refund may be in flight, §9).</summary>
public sealed record BookingCancelled(
    Guid EventId, long BookingId, string Reference, decimal RefundAmount, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.cancelled";
}

/// <summary>The outcome of a confirm transition.</summary>
public sealed record ConfirmResult(long BookingId, string Reference, string Status);

/// <summary>The outcome of a cancellation.</summary>
public sealed record CancelResult(long BookingId, string Reference, string Status, decimal RefundAmount);
