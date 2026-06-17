using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Reviews.Contracts;

namespace Stay.Reviews.Infrastructure;

/// <summary>
/// Verified reviews (BR-6): only the guest of a COMPLETED stay may review it, exactly once. Eligibility
/// is checked against the <c>reviews.reviewable_stay</c> read model (fed by BookingCompleted events) —
/// no cross-context read into booking. The review insert + the "reviewed" flag commit together.
/// </summary>
public sealed class ReviewService(string connectionString)
{
    public async Task<Result<ReviewResponse>> SubmitAsync(
        long guestId, SubmitReviewRequest request, CancellationToken ct = default)
    {
        if (request.OverallRating is < 1 or > 5)
            return Error.Validation("Overall rating must be between 1 and 5.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var stay = await conn.QuerySingleOrDefaultAsync<ReviewableRow>(new CommandDefinition("""
            SELECT booking_id AS BookingId, guest_id AS GuestId, property_id AS PropertyId, reviewed AS Reviewed
            FROM reviews.reviewable_stay WHERE booking_id = @BookingId FOR UPDATE
            """, new { request.BookingId }, tx, cancellationToken: ct));

        // Same response whether there's no completed stay or it isn't the caller's — don't leak (BR-9).
        if (stay is null || stay.GuestId != guestId)
            return Error.NotFound("not-reviewable", "No completed stay is available to review for this booking.");
        if (stay.Reviewed)
            return Error.Conflict("already-reviewed", "This stay has already been reviewed.");

        // New reviews await moderation before they appear publicly (status PENDING → PUBLISHED).
        var review = await conn.QuerySingleAsync<ReviewResponse>(new CommandDefinition("""
            INSERT INTO reviews.review (booking_id, property_id, guest_id, overall_rating, title, body, status)
            VALUES (@BookingId, @propertyId, @guestId, @OverallRating, @Title, @Body, 'PENDING')
            RETURNING id AS Id, property_id AS PropertyId, guest_id AS GuestId, overall_rating AS OverallRating,
                      title AS Title, body AS Body, status AS Status, created_at AS CreatedAt
            """,
            new { request.BookingId, propertyId = stay.PropertyId, guestId, request.OverallRating, request.Title, request.Body },
            tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE reviews.reviewable_stay SET reviewed = true WHERE booking_id = @BookingId",
            new { request.BookingId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<ReviewResponse>.Success(review);
    }

    /// <summary>Published reviews for a property, newest first (paginated).</summary>
    public async Task<IReadOnlyList<ReviewResponse>> GetForPropertyAsync(
        long propertyId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        return (await conn.QueryAsync<ReviewResponse>(new CommandDefinition("""
            SELECT id AS Id, property_id AS PropertyId, guest_id AS GuestId, overall_rating AS OverallRating,
                   title AS Title, body AS Body, status AS Status, created_at AS CreatedAt
            FROM reviews.review
            WHERE property_id = @propertyId AND status = 'PUBLISHED'
            ORDER BY created_at DESC
            OFFSET @offset LIMIT @pageSize
            """, new { propertyId, offset = page * pageSize, pageSize }, cancellationToken: ct))).AsList();
    }

    /// <summary>The published-review rating aggregate for a property (zeros if none yet).</summary>
    public async Task<RatingAggregateResponse> GetAggregateAsync(long propertyId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RatingAggregateResponse>(new CommandDefinition("""
            SELECT property_id AS PropertyId, review_count AS ReviewCount, avg_overall AS AvgOverall
            FROM reviews.property_rating_aggregate WHERE property_id = @propertyId
            """, new { propertyId }, cancellationToken: ct))
            ?? new RatingAggregateResponse(propertyId, 0, 0m);
    }

    private sealed record ReviewableRow(long BookingId, long GuestId, long PropertyId, bool Reviewed);
}
