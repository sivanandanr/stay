using Stay.BuildingBlocks.Messaging;

namespace Stay.Channel.Contracts;

// ── Connection & mapping (owner-facing setup) ─────────────────────────────────

/// <summary>Body for <c>POST /api/v1/channels</c> — connect a property to a channel manager / PMS.</summary>
public sealed record ConnectChannelRequest(long PropertyId, string Provider, string CredentialsRef);

/// <summary>A registered channel connection.</summary>
public sealed record ChannelConnectionResponse(long Id, long PropertyId, string Provider, string Status);

/// <summary>Body for <c>POST /api/v1/channels/{id}/rooms</c> — map an external room/rate code to ours.</summary>
public sealed record MapRoomRequest(
    string ExternalRoomCode, long RoomTypeId, string? ExternalRateCode, long? RatePlanId);

// ── Inbound ARI ingest (channel → platform) ──────────────────────────────────

/// <summary>
/// One availability/rate update for a mapped external room over <c>[From, ToExclusive)</c>. A null
/// field is "leave unchanged"; at least one of <see cref="Allotment"/> / <see cref="BasePrice"/> applies.
/// </summary>
public sealed record AriUpdate(
    string ExternalRoomCode, DateOnly From, DateOnly ToExclusive,
    int? Allotment, decimal? BasePrice, string? Currency, string? ExternalRateCode);

/// <summary>
/// An ordered, idempotent ARI message from a channel manager. <see cref="MessageSeq"/> is the
/// channel's monotonic sequence for this connection — the platform applies strictly increasing
/// sequences and drops anything at-or-below the last applied one (ordered + replay-safe, Gate G5).
/// </summary>
public sealed record AriIngestMessage(long MessageSeq, IReadOnlyList<AriUpdate> Updates);

/// <summary>How an inbound ARI message was handled (mirrors <c>channel.ari_sync_log.status</c>).</summary>
public enum IngestOutcome
{
    /// <summary>Sequence advanced; all updates applied to the ARI calendars.</summary>
    Applied,
    /// <summary>Sequence ≤ last applied — a stale or replayed message; ignored idempotently.</summary>
    DroppedStale,
    /// <summary>An update referenced an unmapped external room/rate code; nothing applied.</summary>
    Quarantined
}

/// <summary>Result of ingesting one ARI message.</summary>
public sealed record IngestResult(IngestOutcome Outcome, long AppliedSeq, string? Detail);

// ── Events (platform → downstream: audit / reverse-sync) ──────────────────────

/// <summary>Emitted when an inbound ARI message is applied — audit evidence + reverse-sync trigger.</summary>
public sealed record ChannelAriApplied(
    Guid EventId, long ChannelConnectionId, long PropertyId, long MessageSeq, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.channel.ari-applied";
}

// ── Channel client port (platform → channel manager) ──────────────────────────

/// <summary>Availability the channel manager reports for one night of a mapped external room.</summary>
public sealed record ChannelAvailability(DateOnly Date, int Available);

/// <summary>
/// Port to the channel manager / PMS. Stay never embeds a vendor SDK — outbound pushes and
/// reconciliation reads go through this port (mirrors the <c>IPaymentGateway</c> rule, §9). A fake
/// backs local/dev/test; per-provider adapters back production.
/// </summary>
public interface IChannelClient
{
    /// <summary>Pushes our availability for <c>[from, toExclusive)</c> to the channel (reverse sync).</summary>
    Task PushAvailabilityAsync(
        long channelConnectionId, string externalRoomCode, DateOnly from, DateOnly toExclusive,
        int available, long messageSeq, CancellationToken ct = default);

    /// <summary>Reads the channel's current availability view for <c>[from, toExclusive)</c> (reconciliation).</summary>
    Task<IReadOnlyList<ChannelAvailability>> GetAvailabilitySnapshotAsync(
        long channelConnectionId, string externalRoomCode, DateOnly from, DateOnly toExclusive,
        CancellationToken ct = default);
}

/// <summary>Body for <c>POST /api/v1/channels/conflicts/{id}/resolve</c> — a resolution note is required (§10).</summary>
public sealed record ResolveConflictRequest(string Resolution, bool Escalate);

/// <summary>A sync conflict after a moderator acts on it.</summary>
public sealed record ConflictResolutionResponse(long Id, string Status, string Resolution);

/// <summary>
/// Emitted when an ops actor resolves (or escalates) a channel sync conflict — audit evidence (§10).
/// The Admin context projects it into <c>admin.audit_log</c>.
/// </summary>
public sealed record ChannelConflictResolved(
    Guid EventId, long ConflictId, long PropertyId, string ConflictType,
    string ActorSub, string Status, string Resolution, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public string EventType => "stay.channel.conflict-resolved";
}

/// <summary>A drift the reconciler found between our availability and the channel's view.</summary>
public sealed record ReconciliationConflict(
    long RoomTypeId, DateOnly Date, int OurAvailable, int ChannelAvailable, string Type);

/// <summary>Outcome of a reconciliation run for one connection.</summary>
public sealed record ReconciliationResult(int NightsChecked, IReadOnlyList<ReconciliationConflict> ConflictsOpened);
