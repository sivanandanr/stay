using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>
/// Raised by the scheduler when a pre-arrival reminder is due for a booking (e.g. T-48h, T-24h),
/// timed in the property timezone (BR-4). The NotificationAdapter turns it into a guest message; the
/// platform owns the WHEN, Notification owns the HOW (§8). Idempotent — emitted once per type.
/// </summary>
public sealed record PreArrivalReminderDue(
    Guid EventId, long BookingId, string Reference, string ReminderType, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.booking.pre_arrival_reminder_due";
}
