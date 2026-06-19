using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Messaging;
using Stay.Reviews.Contracts;

namespace Stay.Reviews.Infrastructure;

/// <summary>
/// Moderates submitted reviews (PENDING → PUBLISHED / REJECTED). Publishing makes the review public
/// and refreshes the property's rating aggregate in the same transaction. Idempotent; authorization
/// (ops/moderator) is enforced at the endpoint. Each decision emits an audit-evidence event to the
/// reviews outbox in the same transaction (§10) — the Admin context records it in <c>admin.audit_log</c>.
/// </summary>
public sealed class ReviewModerationService(string connectionString)
{
    public Task<Result<ReviewResponse>> PublishAsync(long reviewId, string actorSub, CancellationToken ct = default) =>
        TransitionAsync(reviewId, "PUBLISHED", actorSub, reason: null, ct);

    public Task<Result<ReviewResponse>> RejectAsync(long reviewId, string actorSub, string reason, CancellationToken ct = default) =>
        TransitionAsync(reviewId, "REJECTED", actorSub, reason, ct);

    private async Task<Result<ReviewResponse>> TransitionAsync(
        long reviewId, string target, string actorSub, string? reason, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var review = await conn.QuerySingleOrDefaultAsync<ReviewResponse>(new CommandDefinition("""
            SELECT id AS Id, property_id AS PropertyId, guest_id AS GuestId, overall_rating AS OverallRating,
                   title AS Title, body AS Body, status AS Status, created_at AS CreatedAt
            FROM reviews.review WHERE id = @reviewId FOR UPDATE
            """, new { reviewId }, tx, cancellationToken: ct));

        if (review is null)
            return Error.NotFound("review-not-found", $"Review {reviewId} was not found.");
        if (review.Status == target)
            return Result<ReviewResponse>.Success(review); // idempotent
        if (review.Status != "PENDING")
            return Error.Conflict("invalid-state", $"A {review.Status} review cannot be moderated.");

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE reviews.review SET status = @target WHERE id = @reviewId",
            new { target, reviewId }, tx, cancellationToken: ct));

        var aggregate = target == "PUBLISHED"
            ? await RefreshAggregateAsync(conn, tx, review.PropertyId, ct)
            : (ReviewCount: 0, AvgOverall: 0m);

        IIntegrationEvent @event = target == "PUBLISHED"
            ? new ReviewPublished(Guid.NewGuid(), reviewId, review.PropertyId, actorSub, DateTimeOffset.UtcNow,
                aggregate.ReviewCount, aggregate.AvgOverall)
            : new ReviewRejected(Guid.NewGuid(), reviewId, review.PropertyId, actorSub, reason!, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO reviews.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event, @event.GetType()) },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<ReviewResponse>.Success(review with { Status = target });
    }

    private static async Task<(int ReviewCount, decimal AvgOverall)> RefreshAggregateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long propertyId, CancellationToken ct) =>
        await conn.QuerySingleAsync<(int ReviewCount, decimal AvgOverall)>(new CommandDefinition("""
            INSERT INTO reviews.property_rating_aggregate (property_id, review_count, avg_overall, updated_at)
            SELECT @propertyId, count(*), COALESCE(avg(overall_rating), 0), now()
            FROM reviews.review WHERE property_id = @propertyId AND status = 'PUBLISHED'
            ON CONFLICT (property_id) DO UPDATE
                SET review_count = EXCLUDED.review_count, avg_overall = EXCLUDED.avg_overall, updated_at = now()
            RETURNING review_count AS ReviewCount, avg_overall AS AvgOverall
            """, new { propertyId }, tx, cancellationToken: ct));
}
