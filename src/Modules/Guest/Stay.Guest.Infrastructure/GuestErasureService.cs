using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Guest.Contracts;

namespace Stay.Guest.Infrastructure;

/// <summary>The outcome of a data-subject erasure: what was anonymized/removed.</summary>
public sealed record ErasureResult(long GuestId, bool AlreadyErased, int TravelersDeleted, int PaymentTokensDeleted);

/// <summary>
/// Right-to-erasure (BR-8). Anonymizes the guest's master PII in one transaction — null out the
/// profile's cached identity attributes, sever <c>identity_sub</c> to a tombstone (so a future login
/// provisions a fresh profile, not the erased one), and delete saved travelers + stored payment tokens
/// — then emits <see cref="GuestErased"/> to the guest outbox so the admin audit log records it (§10)
/// and the booking context anonymizes its contact snapshots. Financial evidence (bookings, payments,
/// the loyalty ledger keyed by the now-pseudonymous guest id) is retained, only personal data is
/// removed. Idempotent: a profile already erased is a no-op success and does not re-emit (BR-5).
/// </summary>
public sealed class GuestErasureService(string connectionString)
{
    public async Task<Result<ErasureResult>> EraseAsync(long guestId, string actorSub, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var profile = await conn.QuerySingleOrDefaultAsync<(long Id, DateTime? ErasedAt)?>(new CommandDefinition(
            "SELECT id AS Id, erased_at AS ErasedAt FROM guest.guest_profile WHERE id = @guestId FOR UPDATE",
            new { guestId }, tx, cancellationToken: ct));

        if (profile is null)
            return Error.NotFound("guest-not-found", $"Guest {guestId} was not found.");
        if (profile.Value.ErasedAt is not null)
            return Result<ErasureResult>.Success(new ErasureResult(guestId, AlreadyErased: true, 0, 0)); // idempotent

        var travelers = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM guest.saved_traveler WHERE guest_id = @guestId", new { guestId }, tx, cancellationToken: ct));
        var tokens = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM guest.payment_method_token WHERE guest_id = @guestId", new { guestId }, tx, cancellationToken: ct));

        // Anonymize the profile: drop the cached PII and sever the identity link to a unique tombstone.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE guest.guest_profile
            SET email_cache = NULL, name_cache = NULL, email_verified_cache = false,
                locale = NULL, preferred_currency = NULL,
                identity_sub = 'erased:' || id,
                erased_at = now(), updated_at = now(), row_version = row_version + 1
            WHERE id = @guestId
            """, new { guestId }, tx, cancellationToken: ct));

        var @event = new GuestErased(
            Guid.NewGuid(), guestId, actorSub, travelers, tokens, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO guest.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<ErasureResult>.Success(new ErasureResult(guestId, AlreadyErased: false, travelers, tokens));
    }
}
