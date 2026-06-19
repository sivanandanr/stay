using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Stay.BuildingBlocks.Http;

namespace Stay.Admin.Infrastructure.Roles;

/// <summary>
/// Maps a token <c>sub</c> to its platform roles from <c>admin.role_assignment</c> and adds them as
/// role claims, so the server authorizes from the role store — not the identity provider (CLAUDE.md
/// §12). Runs on every authenticated request (ASP.NET claims transformation); token-carried roles are
/// preserved (additive), and a per-<c>sub</c> 60s cache keeps the hot path off the database (§11).
/// Idempotent: the transform may run more than once per request, so a marker claim guards re-resolution.
/// Resilient: a role-store lookup failure degrades to token-carried roles (and fails closed for ops
/// endpoints) rather than 500-ing every authenticated request — the failure isn't cached, so it retries.
/// </summary>
public sealed class RoleClaimsTransformation(
    RoleService roles, IMemoryCache cache, ILogger<RoleClaimsTransformation> logger) : IClaimsTransformation
{
    private const string ResolvedMarker = "stay:roles-resolved";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return principal;
        if (identity.HasClaim(c => c.Type == ResolvedMarker))
            return principal; // already resolved on this principal

        var sub = principal.Subject();
        if (string.IsNullOrWhiteSpace(sub))
            return principal;

        string[] roleCodes;
        try
        {
            roleCodes = await cache.GetOrCreateAsync($"stay:roles:{sub}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                var assignments = await roles.GetRolesAsync(sub);
                return assignments.Select(a => a.RoleCode).Distinct().ToArray();
            }) ?? [];
        }
        catch (Exception ex)
        {
            // Don't fail the request on a role-store hiccup; the throw bypasses the cache so it retries.
            logger.LogWarning(ex, "Could not resolve platform roles for {Sub}; proceeding with token roles only.", sub);
            roleCodes = [];
        }

        identity.AddClaim(new Claim(ResolvedMarker, "1"));
        foreach (var code in roleCodes)
            if (!principal.IsInRole(code))
                identity.AddClaim(new Claim(identity.RoleClaimType, code));

        return principal;
    }
}
