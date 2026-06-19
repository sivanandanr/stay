using System.Text.Json;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Loyalty.Infrastructure;

/// <summary>
/// Earns loyalty points when a stay completes (Phase 9): consumes <see cref="BookingCompleted"/> and
/// credits the guest 1 point per 10 units of spend. Idempotent — the earn is keyed by
/// <c>earn:booking:{id}</c>, so an at-least-once redelivery credits exactly once (BR-5). Event-carried
/// state (the booking total rides on the event) keeps this off the booking context's tables (BR-6).
/// </summary>
public sealed class LoyaltyEarnProjection(LoyaltyService loyalty)
{
    public static bool Handles(string eventType) => eventType == "stay.booking.completed";

    /// <summary>1 point per 10 units of currency spent (floored).</summary>
    public static int PointsFor(decimal totalAmount) => (int)Math.Floor(totalAmount / 10m);

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var e = JsonSerializer.Deserialize<BookingCompleted>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty BookingCompleted payload.");

        var points = PointsFor(e.TotalAmount);
        if (points <= 0)
            return; // no spend to reward

        await loyalty.EarnAsync(
            e.GuestId, points, idempotencyKey: $"earn:booking:{e.BookingId}",
            reason: "stay completed", reference: e.BookingId.ToString(), ct);
    }
}
