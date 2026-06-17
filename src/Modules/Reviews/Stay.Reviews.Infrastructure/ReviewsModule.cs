using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Guest.Contracts;
using Stay.Reviews.Contracts;

namespace Stay.Reviews.Infrastructure;

/// <summary>Reviews context: verified post-stay reviews (submission) + public property review listings.</summary>
public sealed class ReviewsModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");

        services.AddSingleton(new ReviewService(connectionString));
        services.AddSingleton(new ReviewModerationService(connectionString));
        services.AddSingleton(new ReviewableStayProjection(connectionString));
        services.AddHostedService<ReviewsConsumer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Guest submits a verified review of a completed stay.
        endpoints.MapPost("/api/v1/reviews", async (
            SubmitReviewRequest request,
            ClaimsPrincipal user,
            IGuestProvisioning guests,
            ReviewService reviews,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            var result = await reviews.SubmitAsync(guest.GuestId, request, ct);
            return result.ToHttp(review => Results.Created($"/api/v1/reviews/{review.Id}", review));
        })
        .RequireAuthorization()
        .WithName("SubmitReview");

        // Anyone can read a property's published reviews + its rating aggregate.
        endpoints.MapGet("/api/v1/properties/{propertyId:long}/reviews", async (
            long propertyId, int? page, int? pageSize, ReviewService reviews, CancellationToken ct) =>
        {
            var items = await reviews.GetForPropertyAsync(
                propertyId, Math.Max(0, (page ?? 1) - 1), Math.Clamp(pageSize ?? 20, 1, 100), ct);
            var aggregate = await reviews.GetAggregateAsync(propertyId, ct);
            return Results.Ok(new { aggregate, items });
        })
        .WithName("GetPropertyReviews");

        // Moderator publishes a submitted review (makes it public + refreshes the rating aggregate).
        endpoints.MapPost("/api/v1/admin/reviews/{reviewId:long}/publish", async (
            long reviewId, ClaimsPrincipal user, ReviewModerationService moderation, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await moderation.PublishAsync(reviewId, sub, ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("PublishReview");

        // Moderator rejects a submitted review (mandatory reason).
        endpoints.MapPost("/api/v1/admin/reviews/{reviewId:long}/reject", async (
            long reviewId, RejectReviewRequest request, ClaimsPrincipal user, ReviewModerationService moderation, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            if (string.IsNullOrWhiteSpace(request.Reason))
                return ResultHttpExtensions.Problem(Error.Validation("A rejection reason is required."));
            return (await moderation.RejectAsync(reviewId, sub, request.Reason, ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("RejectReview");
    }
}
