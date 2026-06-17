using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.Admin.Infrastructure.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.Admin.Infrastructure.Partners;
using Stay.Admin.Infrastructure.Roles;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;

namespace Stay.Admin.Infrastructure;

/// <summary>
/// Admin context: projects privileged-action events into <c>admin.audit_log</c>, and administers
/// platform roles in <c>admin.role_assignment</c> (the server-side authorization source, §12) —
/// every grant/revoke audited.
/// </summary>
public sealed class AdminModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");

        services.AddDbContext<AdminDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAuditProjection, HostApprovalAuditProjection>();
        services.AddScoped<IAuditProjection, PropertyModerationAuditProjection>();
        services.AddScoped<IAuditProjection, ReviewModerationAuditProjection>();
        services.AddScoped<IAuditProjection, ChannelConflictAuditProjection>();
        services.AddScoped<IAuditProjection, PayoutAuditProjection>();
        services.AddScoped<IAuditProjection, BookingOverrideAuditProjection>();
        services.AddScoped<IAuditProjection, DisputeAuditProjection>();
        services.AddHostedService<AdminAuditConsumer>();
        services.AddSingleton(new RoleService(connectionString));

        // Authorize from the role store, not the token: resolve admin.role_assignment → role claims (§12).
        services.AddMemoryCache();
        services.AddSingleton<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleClaimsTransformation>();

        services.AddSingleton(new PartnerService(connectionString));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Admin grants a platform role to an identity (audited §10).
        endpoints.MapPost("/api/v1/admin/roles", async (
            RoleGrantRequest request, ClaimsPrincipal user, RoleService roles, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await roles.GrantAsync(sub, request, ct)).ToHttp(a => Results.Ok(a));
        })
        .RequireAuthorization("ops")
        .WithName("GrantRole");

        // Admin revokes a platform role (audited §10).
        endpoints.MapPost("/api/v1/admin/roles/revoke", async (
            RoleGrantRequest request, ClaimsPrincipal user, RoleService roles, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await roles.RevokeAsync(sub, request, ct)).ToHttp(removed => Results.Ok(new { removed }));
        })
        .RequireAuthorization("ops")
        .WithName("RevokeRole");

        // Admin lists the roles an identity holds.
        endpoints.MapGet("/api/v1/admin/users/{identitySub}/roles", async (
            string identitySub, RoleService roles, CancellationToken ct) =>
            Results.Ok(await roles.GetRolesAsync(identitySub, ct)))
        .RequireAuthorization("ops")
        .WithName("GetUserRoles");

        // Ops registers a distribution partner (audited §10).
        endpoints.MapPost("/api/v1/admin/partners", async (
            RegisterPartnerRequest request, ClaimsPrincipal user, PartnerService partners, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await partners.RegisterAsync(sub, request, ct)).ToHttp(p => Results.Created($"/api/v1/admin/partners/{p.Id}", p));
        })
        .RequireAuthorization("ops")
        .WithName("RegisterPartner");

        // A partner (client-credentials) prices a base amount: their sell price + the platform net.
        endpoints.MapGet("/api/v1/partner/pricing", async (
            decimal amount, string? currency, ClaimsPrincipal user, PartnerService partners, CancellationToken ct) =>
        {
            var clientId = user.ClientId();
            if (string.IsNullOrWhiteSpace(clientId))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no client_id claim.", ErrorType.Unauthorized));
            return (await partners.PriceAsync(clientId, amount, currency ?? "INR", ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization("partner")
        .WithName("PartnerPricing");
    }
}
