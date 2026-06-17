using Stay.BuildingBlocks.Messaging;

namespace Stay.Booking.Contracts;

/// <summary>Body for <c>POST /api/v1/admin/bookings/{id}/override</c> — an ops status correction (reason mandatory, §10).</summary>
public sealed record ManualOverrideRequest(string ToStatus, string Reason);

/// <summary>The booking's status after a manual override.</summary>
public sealed record ManualOverrideResult(long BookingId, string FromStatus, string ToStatus);

/// <summary>
/// Emitted when ops manually changes a booking's status — audit evidence (§10). The Admin context
/// records it in <c>admin.audit_log</c>; the booking's <c>status_history</c> carries the operational trail.
/// </summary>
public sealed record BookingOverridden(
    Guid EventId, long BookingId, string Reference, string ActorSub,
    string FromStatus, string ToStatus, string Reason, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "stay.booking.overridden";
}
