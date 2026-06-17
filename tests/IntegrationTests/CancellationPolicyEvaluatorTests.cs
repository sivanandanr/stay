using Stay.Ari.Infrastructure.Cancellation;

namespace Stay.IntegrationTests;

/// <summary>Pure BR-4/BR-7 tier evaluation — no database.</summary>
public sealed class CancellationPolicyEvaluatorTests
{
    // 100% if ≥48h before check-in, 50% if ≥24h, otherwise 0%.
    private static readonly CancellationPolicy Standard = new(
        IsRefundable: true,
        Tiers: [new CancellationTier(48, 100), new CancellationTier(24, 50)]);

    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly TimeOnly CheckInTime = new(14, 0); // 2pm local

    // Check-in is 2030-06-10 14:00 in Asia/Singapore (UTC+8) = 2030-06-10 06:00Z.
    private static int Eval(CancellationPolicy policy, DateTimeOffset now, string tz = "Asia/Singapore") =>
        CancellationPolicyEvaluator.RefundPercent(policy, CheckIn, CheckInTime, tz, now);

    [Fact]
    public void Far_in_advance_is_fully_refundable() =>
        Assert.Equal(100, Eval(Standard, new DateTimeOffset(2030, 6, 7, 0, 0, 0, TimeSpan.Zero))); // ~78h before

    [Fact]
    public void Inside_the_first_tier_is_partially_refundable() =>
        Assert.Equal(50, Eval(Standard, new DateTimeOffset(2030, 6, 9, 6, 0, 0, TimeSpan.Zero))); // ~24h before

    [Fact]
    public void Last_minute_is_not_refundable() =>
        Assert.Equal(0, Eval(Standard, new DateTimeOffset(2030, 6, 10, 0, 0, 0, TimeSpan.Zero))); // ~6h before

    [Fact]
    public void At_check_in_or_later_is_not_refundable() =>
        Assert.Equal(0, Eval(Standard, new DateTimeOffset(2030, 6, 11, 0, 0, 0, TimeSpan.Zero))); // after check-in

    [Fact]
    public void Exactly_at_a_threshold_qualifies_for_that_tier() =>
        // 48h before 06:00Z check-in = 2030-06-08 06:00Z.
        Assert.Equal(100, Eval(Standard, new DateTimeOffset(2030, 6, 8, 6, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Non_refundable_policy_always_returns_zero() =>
        Assert.Equal(0, Eval(new CancellationPolicy(false, Standard.Tiers),
            new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Property_timezone_changes_the_lead_time_and_the_tier()
    {
        // 'now' fixed at 2030-06-08 09:00Z. Check-in 14:00 local on 2030-06-10.
        var now = new DateTimeOffset(2030, 6, 8, 9, 0, 0, TimeSpan.Zero);

        // Singapore (UTC+8): check-in = 06:00Z 06-10 → ~45h lead → only the 24h tier (50%).
        Assert.Equal(50, Eval(Standard, now, "Asia/Singapore"));

        // Pacific/Auckland (UTC+12): check-in = 02:00Z 06-10 → ~41h lead → still 50%, but a wider-tz
        // case crossing 48h shows the timezone matters. Honolulu (UTC-10) pushes lead past 48h → 100%.
        Assert.Equal(100, Eval(Standard, now, "Pacific/Honolulu"));
    }
}
