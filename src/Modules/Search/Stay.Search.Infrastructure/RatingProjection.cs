using System.Text.Json;
using Stay.BuildingBlocks.Outbox;
using Stay.Reviews.Contracts;

namespace Stay.Search.Infrastructure;

/// <summary>
/// Keeps the search index's rating signal fresh: when a review is published, the new aggregate rides on
/// <see cref="ReviewPublished"/> (event-carried state, BR-6), so the search card stars and the
/// min-rating filter reflect real reviews without reading back into the Reviews context. Eventually
/// consistent + idempotent (partial-doc update with the latest aggregate).
/// </summary>
public sealed class RatingProjection(IPropertySearchIndex index)
{
    public static bool Handles(string eventType) => eventType == "stay.reviews.review-published";

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var e = JsonSerializer.Deserialize<ReviewPublished>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty ReviewPublished payload.");

        if (e.PropertyId <= 0)
            return;

        await index.UpdateRatingAsync(e.PropertyId, (double)e.AvgOverall, e.ReviewCount, ct);
    }
}
