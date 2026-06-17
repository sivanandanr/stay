namespace Stay.Admin.Domain.Auditing;

/// <summary>
/// Inbox marker (<c>admin.processed_event</c>): records that an integration event has been projected,
/// so redelivery under at-least-once doesn't duplicate the audit row (BR-5).
/// </summary>
public sealed class ProcessedEvent
{
    private ProcessedEvent() { } // EF materialization

    public string EventId { get; private set; } = null!;
    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedEvent Of(Guid eventId) => new() { EventId = eventId.ToString() };
}
