using System.Collections.Concurrent;
using Stay.Channel.Contracts;

namespace Stay.Channel.Infrastructure;

/// <summary>
/// Local/dev/test stand-in for a channel manager (no real vendor SDK in Stay — see
/// <see cref="IChannelClient"/>). Records outbound pushes and serves a settable availability snapshot
/// so reconciliation has something to diff against. Production swaps in per-provider adapters.
/// </summary>
public sealed class FakeChannelClient : IChannelClient
{
    private readonly ConcurrentDictionary<(long Conn, string Room, DateOnly Date), int> _snapshot = new();

    /// <summary>Outbound pushes the platform made, in order — assertable in tests.</summary>
    public List<(long Conn, string Room, DateOnly From, DateOnly ToExclusive, int Available, long Seq)> Pushes { get; } = [];

    /// <summary>Seeds what the channel "reports" as available for a night (test/dev hook).</summary>
    public void SetChannelAvailability(long channelConnectionId, string externalRoomCode, DateOnly date, int available) =>
        _snapshot[(channelConnectionId, externalRoomCode, date)] = available;

    public Task PushAvailabilityAsync(
        long channelConnectionId, string externalRoomCode, DateOnly from, DateOnly toExclusive,
        int available, long messageSeq, CancellationToken ct = default)
    {
        Pushes.Add((channelConnectionId, externalRoomCode, from, toExclusive, available, messageSeq));
        for (var d = from; d < toExclusive; d = d.AddDays(1))
            _snapshot[(channelConnectionId, externalRoomCode, d)] = available;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChannelAvailability>> GetAvailabilitySnapshotAsync(
        long channelConnectionId, string externalRoomCode, DateOnly from, DateOnly toExclusive,
        CancellationToken ct = default)
    {
        var result = new List<ChannelAvailability>();
        for (var d = from; d < toExclusive; d = d.AddDays(1))
            result.Add(new ChannelAvailability(d, _snapshot.GetValueOrDefault((channelConnectionId, externalRoomCode, d), 0)));
        return Task.FromResult<IReadOnlyList<ChannelAvailability>>(result);
    }
}
