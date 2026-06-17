using System.Security.Claims;

namespace Stay.BuildingBlocks.Http;

/// <summary>Reads the identity claims the platform cares about, tolerating the usual claim-name variants.</summary>
public static class TokenClaims
{
    public static string? Subject(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? Email(this ClaimsPrincipal user) =>
        user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);

    public static string? Name(this ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name);

    public static bool EmailVerified(this ClaimsPrincipal user) =>
        bool.TryParse(user.FindFirstValue("email_verified"), out var verified) && verified;

    /// <summary>The OAuth client identity (machine-to-machine, e.g. a partner) — the <c>client_id</c>/<c>azp</c> claim.</summary>
    public static string? ClientId(this ClaimsPrincipal user) =>
        user.FindFirstValue("client_id") ?? user.FindFirstValue("azp");
}
