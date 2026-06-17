namespace Stay.BuildingBlocks.Messaging;

/// <summary>
/// A domain event published across module / service boundaries. Modules depend on this
/// abstraction (and <see cref="IEventPublisher"/>), never on the broker SDK — keeping the
/// broker swappable and the boundaries enforceable (CLAUDE.md §5).
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>
    /// Stable identity of this event. It is the idempotency key carried end to end:
    /// the outbox row id, the Kafka message key, and the consumer's dedupe key (BR-5).
    /// </summary>
    Guid EventId { get; }

    /// <summary>The wire contract name, e.g. <c>stay.catalog.test-event</c>.</summary>
    string EventType { get; }
}
