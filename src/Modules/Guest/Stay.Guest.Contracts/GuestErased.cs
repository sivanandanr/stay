using Stay.BuildingBlocks.Messaging;

namespace Stay.Guest.Contracts;

/// <summary>
/// Raised when a data-subject erasure (BR-8) anonymizes a guest's PII. Written to the guest outbox in
/// the same transaction as the anonymization — the durable, append-only evidence of the privileged
/// action (CLAUDE.md §10). An admin.audit_log projection records it, and the booking context reacts by
/// anonymizing its contact snapshots. Carries only counts (no PII) so the event itself leaks nothing.
/// </summary>
public sealed record GuestErased(
    Guid EventId,
    long GuestId,
    string ActorSub,
    int TravelersDeleted,
    int PaymentTokensDeleted,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "stay.guest.erased";
}
