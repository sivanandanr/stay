using Stay.BuildingBlocks.Outbox;

namespace Stay.BuildingBlocks.Messaging;

/// <summary>
/// Publishes an already-persisted outbox message to the broker. Implemented by the
/// infrastructure (Kafka); callers never touch the broker SDK directly.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct = default);
}
