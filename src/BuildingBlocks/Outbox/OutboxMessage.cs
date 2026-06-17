namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// One row of a context's <c>outbox_message</c> table. Written in the same transaction as the
/// state change that produced it (no dual-write, BR-11), then drained by the dispatcher.
/// </summary>
public sealed record OutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ProcessedAt);
