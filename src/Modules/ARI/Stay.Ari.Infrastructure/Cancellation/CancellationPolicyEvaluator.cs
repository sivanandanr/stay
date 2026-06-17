namespace Stay.Ari.Infrastructure.Cancellation;

/// <summary>
/// Evaluates a cancellation policy to a refund percentage (BR-7). The lead time is measured to the
/// check-in moment in the PROPERTY's IANA timezone (BR-4) — never the server's or guest's. The tier
/// with the highest <c>hours_before_checkin</c> threshold the lead time still satisfies wins; a
/// non-refundable policy, an empty policy, or a cancellation at/after check-in yields 0%.
/// </summary>
public static class CancellationPolicyEvaluator
{
    public static int RefundPercent(
        CancellationPolicy policy,
        DateOnly checkInDate,
        TimeOnly checkInTime,
        string propertyTimeZone,
        DateTimeOffset now)
    {
        if (!policy.IsRefundable || policy.Tiers.Count == 0)
            return 0;

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(propertyTimeZone);
        var localCheckIn = checkInDate.ToDateTime(checkInTime); // DateTimeKind.Unspecified — a wall-clock time
        var checkInInstant = new DateTimeOffset(localCheckIn, timeZone.GetUtcOffset(localCheckIn));

        var leadHours = (checkInInstant - now).TotalHours;

        var applicable = policy.Tiers
            .Where(tier => leadHours >= tier.HoursBeforeCheckin)
            .OrderByDescending(tier => tier.HoursBeforeCheckin)
            .FirstOrDefault();

        return applicable?.RefundPct ?? 0;
    }
}
