using Dapper;
using Npgsql;
using Stay.Guest.Contracts;

namespace Stay.Guest.Infrastructure;

/// <summary>
/// Provisions guest profiles in <c>guest.guest_profile</c> on first login. Idempotent and race-safe
/// (BR-5, P0-B4): a replay returns the existing profile; concurrent first-requests resolve to exactly
/// one row via the <c>identity_sub</c> unique constraint — the loser re-reads the winner.
/// </summary>
public sealed class GuestProvisioningService(string connectionString) : IGuestProvisioning
{
    public async Task<GuestProfile> ProvisionAsync(
        string identitySub, string? email, string? name, bool emailVerified, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var existing = await ReadAsync(conn, identitySub, ct);
        if (existing is not null)
            return existing;

        try
        {
            var profile = await conn.QuerySingleAsync<GuestProfile>(new CommandDefinition("""
                INSERT INTO guest.guest_profile (identity_sub, email_cache, name_cache, email_verified_cache)
                VALUES (@identitySub, @email, @name, @emailVerified)
                RETURNING id AS GuestId, email_cache AS Email, email_verified_cache AS EmailVerified
                """, new { identitySub, email, name, emailVerified }, cancellationToken: ct));
            return profile;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost the race — another request created the profile first; return it (BR-5).
            return await ReadAsync(conn, identitySub, ct)
                ?? throw new InvalidOperationException("Guest insert conflicted but no profile was found.", ex);
        }
    }

    private static async Task<GuestProfile?> ReadAsync(NpgsqlConnection conn, string identitySub, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<GuestProfile>(new CommandDefinition("""
            SELECT id AS GuestId, email_cache AS Email, email_verified_cache AS EmailVerified
            FROM guest.guest_profile WHERE identity_sub = @identitySub
            """, new { identitySub }, cancellationToken: ct));
}
