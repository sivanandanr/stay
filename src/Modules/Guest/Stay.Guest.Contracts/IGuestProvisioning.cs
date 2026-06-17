namespace Stay.Guest.Contracts;

/// <summary>The caller's platform guest profile (keyed internally by the identity sub).</summary>
public sealed record GuestProfile(long GuestId, string? Email, bool EmailVerified);

/// <summary>
/// First-login provisioning (UC-5.1a / P0-B4): maps an authenticated identity <c>sub</c> to a guest
/// profile, creating it on first sight. Idempotent and race-safe (BR-5). Identity attributes are
/// cached from the token, never mastered here.
/// </summary>
public interface IGuestProvisioning
{
    Task<GuestProfile> ProvisionAsync(
        string identitySub, string? email, string? name, bool emailVerified, CancellationToken ct = default);
}
