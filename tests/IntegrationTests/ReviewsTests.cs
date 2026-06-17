using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Booking.Contracts;
using Stay.Reviews.Contracts;
using Stay.Reviews.Infrastructure;
using Stay.BuildingBlocks;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Phase 6 verified reviews: a completed stay becomes reviewable, and only its guest may review it once (BR-6).</summary>
public sealed class ReviewsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

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

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent @event) =>
        new(@event.EventId, @event.EventType, JsonSerializer.Serialize(@event, @event.GetType()), DateTimeOffset.UtcNow);

    /// <summary>Marks a stay reviewable by projecting a BookingCompleted event (as the consumer would).</summary>
    private Task MakeReviewableAsync(long bookingId, long guestId, long propertyId) =>
        _projection.ProjectAsync(EnvelopeFor(new BookingCompleted(Guid.NewGuid(), bookingId, guestId, propertyId, DateTimeOffset.UtcNow)));

    private async Task<int> ReviewCountAsync(long propertyId)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM reviews.review WHERE property_id=@propertyId", new { propertyId });
    }

    [Fact]
    public async Task A_completed_stay_can_be_reviewed_by_its_guest()
    {
        await MakeReviewableAsync(bookingId: 100, guestId: 7, propertyId: 55);

        var result = await _reviews.SubmitAsync(guestId: 7,
            new SubmitReviewRequest(BookingId: 100, OverallRating: 5, Title: "Lovely", Body: "Great stay."));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(5, result.Value!.OverallRating);
        Assert.Equal(55, result.Value!.PropertyId);
        Assert.Equal("PENDING", result.Value!.Status); // awaits moderation before going public
    }

    [Fact]
    public async Task A_stay_can_only_be_reviewed_once()
    {
        await MakeReviewableAsync(100, 7, 55);
        await _reviews.SubmitAsync(7, new SubmitReviewRequest(100, 5, null, "First."));

        var second = await _reviews.SubmitAsync(7, new SubmitReviewRequest(100, 4, null, "Again."));

        Assert.False(second.IsSuccess);
        Assert.Equal("already-reviewed", second.Error!.Value.Code);
        Assert.Equal(1, await ReviewCountAsync(55));
    }

    [Fact]
    public async Task A_guest_cannot_review_a_stay_that_is_not_theirs()
    {
        await MakeReviewableAsync(100, guestId: 7, propertyId: 55);

        var result = await _reviews.SubmitAsync(guestId: 999, new SubmitReviewRequest(100, 5, null, "Sneaky."));

        Assert.False(result.IsSuccess);
        Assert.Equal("not-reviewable", result.Error!.Value.Code); // don't leak that the stay exists
    }

    [Fact]
    public async Task Reviewing_without_a_completed_stay_is_rejected()
    {
        var result = await _reviews.SubmitAsync(7, new SubmitReviewRequest(BookingId: 12345, OverallRating: 5, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("not-reviewable", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Rating_must_be_between_one_and_five()
    {
        await MakeReviewableAsync(100, 7, 55);

        var result = await _reviews.SubmitAsync(7, new SubmitReviewRequest(100, OverallRating: 6, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Only_moderated_reviews_are_listed_and_the_aggregate_reflects_them()
    {
        await MakeReviewableAsync(100, 7, 55);
        await MakeReviewableAsync(101, 8, 55);
        var a = (await _reviews.SubmitAsync(7, new SubmitReviewRequest(100, 4, "A", null))).Value!;
        await _reviews.SubmitAsync(8, new SubmitReviewRequest(101, 5, "B", null));

        // Nothing public until moderated.
        Assert.Empty(await _reviews.GetForPropertyAsync(55, 0, 20));

        await _moderation.PublishAsync(a.Id, "admin|1"); // publish only the first

        var list = await _reviews.GetForPropertyAsync(55, 0, 20);
        Assert.Single(list);
        Assert.Equal("PUBLISHED", list[0].Status);

        var aggregate = await _reviews.GetAggregateAsync(55);
        Assert.Equal(1, aggregate.ReviewCount);
        Assert.Equal(4m, aggregate.AvgOverall);
    }

    [Fact]
    public async Task Projection_is_idempotent()
    {
        await MakeReviewableAsync(100, 7, 55);
        await MakeReviewableAsync(100, 7, 55); // redelivery

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM reviews.reviewable_stay WHERE booking_id=100"));
    }
}
