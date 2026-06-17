namespace Stay.Ari.Infrastructure.Cancellation;

/// <summary>One refund tier: cancel at least <see cref="HoursBeforeCheckin"/> hours before check-in for <see cref="RefundPct"/>%.</summary>
public sealed record CancellationTier(int HoursBeforeCheckin, int RefundPct);

/// <summary>A property's cancellation policy (mirrors <c>ari.cancellation_policy</c>: tiers + refundability).</summary>
public sealed record CancellationPolicy(bool IsRefundable, IReadOnlyList<CancellationTier> Tiers);
