using System.Collections.Concurrent;

namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// In-memory at-least-once dedupe for the demo consumer: the first sighting of an event id wins,
/// redeliveries are dropped. A production consumer (e.g. NotificationAdapter) persists this ledger
/// — <c>notify.notification_emission</c> — so idempotency survives restarts (BR-5, CLAUDE.md §8).
/// </summary>
public sealed class IdempotentReceiver
{
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();

    /// <summary>Returns true the first time an event id is seen, false on every redelivery.</summary>
    public bool TryBegin(Guid eventId) => _seen.TryAdd(eventId, 0);
}
