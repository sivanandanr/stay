using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Booking.Contracts;
using Stay.Reviews.Contracts;
using Stay.Reviews.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Review moderation: PENDING → PUBLISHED/REJECTED, with the rating aggregate refreshed on publish.</summary>
public sealed class ReviewModerationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private const string Actor = "admin|9";

    private ReviewableStayProjection _projection = null!;
    private ReviewService _reviews = null!;
    private ReviewModerationService _moderation = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(ReviewsSchema.Ddl);
        _projection = new ReviewableStayProjection(_postgres.GetConnectionString());
        _reviews = new ReviewService(_postgres.GetConnectionString());
        _moderation = new ReviewModerationService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent e) =>
        new(e.EventId, e.EventType, JsonSerializer.Serialize(e, e.GetType()), DateTimeOffset.UtcNow);

    /// <summary>Makes a stay reviewable and submits a PENDING review; returns its id.</summary>
    private async Task<long> SubmitAsync(long bookingId, long guestId, long propertyId, short rating)
    {
        await _projection.ProjectAsync(EnvelopeFor(new BookingCompleted(Guid.NewGuid(), bookingId, guestId, propertyId, DateTimeOffset.UtcNow)));
        var review = await _reviews.SubmitAsync(guestId, new SubmitReviewRequest(bookingId, rating, null, null));
        return review.Value!.Id;
    }

    [Fact]
    public async Task Publishing_makes_the_review_public_and_updates_the_aggregate()
    {
        var r1 = await SubmitAsync(100, 7, 55, 4);
        var r2 = await SubmitAsync(101, 8, 55, 2);

        await _moderation.PublishAsync(r1, Actor);
        await _moderation.PublishAsync(r2, Actor);

        var aggregate = await _reviews.GetAggregateAsync(55);
        Assert.Equal(2, aggregate.ReviewCount);
        Assert.Equal(3.00m, aggregate.AvgOverall); // (4 + 2) / 2
    }

    [Fact]
    public async Task Publishing_is_idempotent()
    {
        var id = await SubmitAsync(100, 7, 55, 5);

        var first = await _moderation.PublishAsync(id, Actor);
        var second = await _moderation.PublishAsync(id, Actor);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, (await _reviews.GetAggregateAsync(55)).ReviewCount); // not double-counted
    }

    [Fact]
    public async Task A_rejected_review_never_becomes_public()
    {
        var id = await SubmitAsync(100, 7, 55, 1);

        var rejected = await _moderation.RejectAsync(id, Actor, "Abusive language.");

        Assert.True(rejected.IsSuccess);
        Assert.Equal("REJECTED", rejected.Value!.Status);
        Assert.Empty(await _reviews.GetForPropertyAsync(55, 0, 20));
        Assert.Equal(0, (await _reviews.GetAggregateAsync(55)).ReviewCount);
    }

    [Fact]
    public async Task A_rejected_review_cannot_then_be_published()
    {
        var id = await SubmitAsync(100, 7, 55, 3);
        await _moderation.RejectAsync(id, Actor, "spam");

        var result = await _moderation.PublishAsync(id, Actor);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid-state", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Moderating_an_unknown_review_is_not_found()
    {
        var result = await _moderation.PublishAsync(999_999, Actor);
        Assert.False(result.IsSuccess);
        Assert.Equal("review-not-found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Moderation_emits_an_audit_event_to_the_reviews_outbox_in_the_same_tx()
    {
        var published = await SubmitAsync(100, 7, 55, 5);
        var rejected = await SubmitAsync(101, 8, 55, 1);

        await _moderation.PublishAsync(published, "admin|publish");
        await _moderation.RejectAsync(rejected, "admin|reject", "Off-topic.");

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();

        var rows = (await conn.QueryAsync<(string Type, string Payload)>(
            "SELECT type AS Type, payload AS Payload FROM reviews.outbox_message ORDER BY occurred_at")).ToList();

        Assert.Equal(2, rows.Count);

        var pub = rows.Single(r => r.Type == "stay.reviews.review-published");
        var pubEvent = JsonSerializer.Deserialize<ReviewPublished>(pub.Payload)!;
        Assert.Equal(published, pubEvent.ReviewId);
        Assert.Equal(55, pubEvent.PropertyId);
        Assert.Equal("admin|publish", pubEvent.ActorSub);

        var rej = rows.Single(r => r.Type == "stay.reviews.review-rejected");
        var rejEvent = JsonSerializer.Deserialize<ReviewRejected>(rej.Payload)!;
        Assert.Equal("admin|reject", rejEvent.ActorSub);
        Assert.Equal("Off-topic.", rejEvent.Reason);
    }
}
