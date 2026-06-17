namespace Stay.Booking.Contracts;

/// <summary>One refund tier (cancel ≥ <see cref="HoursBeforeCheckin"/>h before check-in → <see cref="RefundPct"/>%).</summary>
public sealed record CancellationTierDto(int HoursBeforeCheckin, int RefundPct);

/// <summary>
/// The cancellation policy frozen onto the booking at hold time (BR-2): the tiers, refundability, and
/// the property's timezone + check-in time needed to evaluate the refund later in the property
/// timezone (BR-4). Supplied by the booking funnel, which already shows the policy to the guest.
/// </summary>
public sealed record CancellationSnapshot(
    string Timezone,
    TimeOnly CheckInTime,
    bool IsRefundable,
    IReadOnlyList<CancellationTierDto> Tiers);
