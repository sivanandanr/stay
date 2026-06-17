using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Guest.Contracts;

namespace Stay.Guest.Infrastructure;

/// <summary>Guest context: first-login profile provisioning (thin integration over Identity).</summary>
public sealed class GuestModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");
        services.AddSingleton<IGuestProvisioning>(new GuestProvisioningService(connectionString));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        // First call provisions the profile from the token claims; subsequent calls return it.
        endpoints.MapGet("/api/v1/guests/me", async (
            ClaimsPrincipal user, IGuestProvisioning guests, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return Results.Unauthorized();

            var profile = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            return Results.Ok(profile);
        })
        .RequireAuthorization()
        .WithName("GetMyGuestProfile");
}
