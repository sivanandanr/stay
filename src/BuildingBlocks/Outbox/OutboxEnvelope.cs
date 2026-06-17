namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// The self-describing JSON shape put on the wire for each outbox message. Carries the
/// idempotency key (<see cref="Id"/>) so any consumer can dedupe without out-of-band state.
/// </summary>
public sealed record OutboxEnvelope(
    Guid Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt);
