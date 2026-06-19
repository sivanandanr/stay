using Dapper;
using Npgsql;
using Stay.BuildingBlocks;

namespace Stay.Loyalty.Infrastructure;

/// <summary>A guest's loyalty standing.</summary>
public sealed record LoyaltyAccount(long GuestId, int Balance, string Tier);

/// <summary>
/// Guest loyalty points (Phase 9). Earn and redeem are idempotent by key (BR-5) — a replayed
/// <c>BookingCompleted</c>-driven earn or a double-submitted redemption applies exactly once — and the
/// running balance plus an append-only ledger keep the two consistent in one transaction. The balance
/// can never go negative: redemption checks under a row lock and the DB <c>CHECK (balance &gt;= 0)</c> is
/// the backstop (mirrors the BR-1 inventory invariant).
/// </summary>
public sealed class LoyaltyService(string connectionString)
{
    public async Task<Result<LoyaltyAccount>> EarnAsync(
        long guestId, int points, string idempotencyKey, string? reason = null, string? reference = null, CancellationToken ct = default)
    {
        if (points <= 0)
            return Error.Validation("Points to earn must be positive.");
        return await ApplyAsync(guestId, "EARN", points, idempotencyKey, reason, reference, ct);
    }

    public async Task<Result<LoyaltyAccount>> RedeemAsync(
        long guestId, int points, string idempotencyKey, string? reason = null, CancellationToken ct = default)
    {
        if (points <= 0)
            return Error.Validation("Points to redeem must be positive.");
        return await ApplyAsync(guestId, "REDEEM", -points, idempotencyKey, reason, reference: null, ct);
    }

    /// <summary>₹ value of one redeemed point. The discount is frozen onto the booking at hold (BR-2).</summary>
    public const decimal PointValue = 1.00m;

    /// <summary>The monetary discount for redeeming <paramref name="points"/> points (deterministic; frozen at hold).</summary>
    public static decimal DiscountFor(int points) => points * PointValue;

    /// <summary>
    /// Redeems points on the CALLER's connection + transaction (no own commit/rollback) so the booking
    /// saga can decrement the balance atomically with the confirm (user-approved in-saga redemption,
    /// mirrors Promotion.RedeemInTransactionAsync). Idempotent by <paramref name="idempotencyKey"/>: a
    /// confirm retry redeems once. Returns a conflict if the guest no longer has enough points, which
    /// rolls back the whole confirm (the points discount was frozen at hold, BR-2).
    /// </summary>
    public async Task<Result<int>> RedeemInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long guestId, int points, string idempotencyKey,
        string? reason = null, string? reference = null, CancellationToken ct = default)
    {
        if (points <= 0)
            return Error.Validation("Points to redeem must be positive.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error.Validation("An idempotency key is required.");

        // Upsert + lock the account row so a concurrent earn/redeem for this guest serializes.
        var account = await conn.QuerySingleAsync<(long Id, int Balance)>(new CommandDefinition("""
            INSERT INTO loyalty.account (guest_id) VALUES (@guestId)
            ON CONFLICT (guest_id) DO UPDATE SET guest_id = EXCLUDED.guest_id
            RETURNING id AS Id, balance AS Balance
            """, new { guestId }, tx, cancellationToken: ct));

        // Idempotent: a replay of the same key has already redeemed → report those points, don't re-apply.
        var already = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM loyalty.ledger WHERE idempotency_key = @idempotencyKey)",
            new { idempotencyKey }, tx, cancellationToken: ct));
        if (already)
            return Result<int>.Success(points);

        if (account.Balance < points)
            return Error.Conflict("insufficient-points", $"Balance {account.Balance} is too low to redeem {points} points.");

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO loyalty.ledger (account_id, type, points, reason, reference, idempotency_key)
            VALUES (@Id, 'REDEEM', @signedPoints, @reason, @reference, @idempotencyKey)
            """, new { account.Id, signedPoints = -points, reason, reference, idempotencyKey }, tx, cancellationToken: ct));

        // Redeem never changes tier (tier follows LIFETIME earned points), so only the balance moves.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE loyalty.account SET balance = balance - @points, updated_at = now() WHERE id = @Id",
            new { points, account.Id }, tx, cancellationToken: ct));

        return Result<int>.Success(points);
    }

    /// <summary>Tier from lifetime earned points. Thresholds are monotonic so a guest never drops a tier by redeeming.</summary>
    public static string TierFor(int lifetimeEarned) => lifetimeEarned switch
    {
        >= 20_000 => "PLATINUM",
        >= 5_000 => "GOLD",
        >= 1_000 => "SILVER",
        _ => "BRONZE"
    };

    public async Task<LoyaltyAccount> GetAsync(long guestId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(int Balance, string Tier)?>(new CommandDefinition(
            "SELECT balance AS Balance, tier AS Tier FROM loyalty.account WHERE guest_id = @guestId",
            new { guestId }, cancellationToken: ct));
        return row is { } a ? new LoyaltyAccount(guestId, a.Balance, a.Tier) : new LoyaltyAccount(guestId, 0, "BRONZE");
    }

    /// <summary>Applies a signed delta (+earn / −redeem) atomically: ledger row + balance, idempotent by key.</summary>
    private async Task<Result<LoyaltyAccount>> ApplyAsync(
        long guestId, string type, int signedPoints, string idempotencyKey, string? reason, string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error.Validation("An idempotency key is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Upsert + lock the account row so concurrent earns/redeems for one guest serialize.
        var account = await conn.QuerySingleAsync<(long Id, int Balance, string Tier)>(new CommandDefinition("""
            INSERT INTO loyalty.account (guest_id) VALUES (@guestId)
            ON CONFLICT (guest_id) DO UPDATE SET guest_id = EXCLUDED.guest_id
            RETURNING id AS Id, balance AS Balance, tier AS Tier
            """, new { guestId }, tx, cancellationToken: ct));

        // Idempotent: a replay of the same key returns the current balance without re-applying.
        var already = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM loyalty.ledger WHERE idempotency_key = @idempotencyKey)",
            new { idempotencyKey }, tx, cancellationToken: ct));
        if (already)
        {
            await tx.CommitAsync(ct);
            return Result<LoyaltyAccount>.Success(new LoyaltyAccount(guestId, account.Balance, account.Tier));
        }

        var newBalance = account.Balance + signedPoints;
        if (newBalance < 0)
            return Error.Conflict("insufficient-points", $"Balance {account.Balance} is too low to redeem {-signedPoints} points.");

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO loyalty.ledger (account_id, type, points, reason, reference, idempotency_key)
            VALUES (@Id, @type, @signedPoints, @reason, @reference, @idempotencyKey)
            """, new { account.Id, type, signedPoints, reason, reference, idempotencyKey }, tx, cancellationToken: ct));

        // Tier follows LIFETIME earned points (sum of EARN rows) — monotonic, so it never downgrades on redeem.
        var tier = account.Tier;
        if (type == "EARN")
        {
            var lifetimeEarned = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(SUM(points), 0) FROM loyalty.ledger WHERE account_id = @Id AND type = 'EARN'",
                new { account.Id }, tx, cancellationToken: ct));
            tier = TierFor(lifetimeEarned);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE loyalty.account SET balance = @newBalance, tier = @tier, updated_at = now() WHERE id = @Id",
            new { newBalance, tier, account.Id }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<LoyaltyAccount>.Success(new LoyaltyAccount(guestId, newBalance, tier));
    }
}
