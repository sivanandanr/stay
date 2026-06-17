using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Promotion.Contracts;

namespace Stay.Promotion.Infrastructure;

/// <summary>
/// Promotion context: ops/host create promotions and issue coupon codes; the guest funnel previews a
/// coupon's effect before paying (the booking saga redeems at confirm). Eventually consistent, cacheable.
/// </summary>
public sealed class PromotionModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");
        services.AddSingleton(new PromotionService(connectionString));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Ops/host creates a promotion.
        endpoints.MapPost("/api/v1/admin/promotions", async (
            CreatePromotionRequest request, PromotionService promotions, CancellationToken ct) =>
            (await promotions.CreateAsync(request, ct)).ToHttp(p => Results.Created($"/api/v1/admin/promotions/{p.Id}", p)))
        .RequireAuthorization("ops")
        .WithName("CreatePromotion");

        // Ops/host issues a coupon code under a promotion.
        endpoints.MapPost("/api/v1/admin/promotions/{promotionId:long}/coupons", async (
            long promotionId, IssueCouponRequest request, PromotionService promotions, CancellationToken ct) =>
            (await promotions.IssueCouponAsync(promotionId, request, ct))
                .ToHttp(c => Results.Created($"/api/v1/admin/coupons/{c.Id}", c)))
        .RequireAuthorization("ops")
        .WithName("IssueCoupon");

        // Guest previews a coupon's effect on an amount (full net shown before pay, §7).
        endpoints.MapPost("/api/v1/coupons/apply", async (
            ApplyCouponRequest request, PromotionService promotions, CancellationToken ct) =>
            (await promotions.ApplyAsync(request.Code, request.Amount, request.Currency, DateTimeOffset.UtcNow, ct))
                .ToHttp(Results.Ok))
        .RequireAuthorization()
        .WithName("ApplyCoupon");
    }
}
