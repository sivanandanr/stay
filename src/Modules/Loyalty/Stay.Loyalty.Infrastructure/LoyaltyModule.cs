using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Guest.Contracts;

namespace Stay.Loyalty.Infrastructure;

/// <summary>Body for <c>POST /api/v1/me/loyalty/redeem</c>.</summary>
public sealed record RedeemPointsRequest(int Points, string IdempotencyKey, string? Reason);

/// <summary>Body for <c>POST /api/v1/admin/loyalty/{guestId}/earn</c> — manual grant (real earning is BookingCompleted-driven).</summary>
public sealed record EarnPointsRequest(int Points, string IdempotencyKey, string? Reason, string? Reference);

/// <summary>Loyalty context: a guest's own points balance + redemption; ops can grant points. Eventually consistent.</summary>
public sealed class LoyaltyModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");
        var loyalty = new LoyaltyService(connectionString);
        services.AddSingleton(loyalty);
        services.AddSingleton(new LoyaltyEarnProjection(loyalty));
        services.AddHostedService<LoyaltyEarnConsumer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The guest's own balance (provisioned from the token).
        endpoints.MapGet("/api/v1/me/loyalty", async (
            ClaimsPrincipal user, IGuestProvisioning guests, LoyaltyService loyalty, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            return Results.Ok(await loyalty.GetAsync(guest.GuestId, ct));
        })
        .RequireAuthorization()
        .WithName("GetMyLoyalty");

        // The guest redeems their own points (idempotent by key).
        endpoints.MapPost("/api/v1/me/loyalty/redeem", async (
            RedeemPointsRequest request, ClaimsPrincipal user, IGuestProvisioning guests, LoyaltyService loyalty, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            return (await loyalty.RedeemAsync(guest.GuestId, request.Points, request.IdempotencyKey, request.Reason, ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("RedeemLoyalty");

        // Ops grants points to a guest (manual; the booking-completed earn pipeline is a follow-up).
        endpoints.MapPost("/api/v1/admin/loyalty/{guestId:long}/earn", async (
            long guestId, EarnPointsRequest request, LoyaltyService loyalty, CancellationToken ct) =>
            (await loyalty.EarnAsync(guestId, request.Points, request.IdempotencyKey, request.Reason, request.Reference, ct))
                .ToHttp(Results.Ok))
        .RequireAuthorization("ops")
        .WithName("EarnLoyalty");
    }
}
