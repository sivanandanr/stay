using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Promotion.Contracts;

namespace Stay.Promotion.Infrastructure;

/// <summary>
/// Promotions &amp; coupons (Phase 8). <see cref="ApplyAsync"/> is the read-only funnel-time preview
/// (validate + compute the discount so the guest sees the net before paying — no dark patterns, §7);
/// <see cref="RedeemAsync"/> is the confirm-time commit, recorded once per booking (idempotent via the
/// <c>(coupon_id, booking_id)</c> unique key) under a coupon row lock so concurrent redemptions can't
/// overshoot a max-redemption or budget cap. The redeemed amount is the contract recorded against the
/// booking (BR-2). PERCENT_OFF / FIXED_OFF supported; FREE_NIGHT is a follow-up.
/// </summary>
public sealed class PromotionService(string connectionString)
{
    public async Task<Result<PromotionResponse>> CreateAsync(CreatePromotionRequest request, CancellationToken ct = default)
    {
        if (request.OwnerType is not ("PLATFORM" or "HOST"))
            return Error.Validation("Owner type must be PLATFORM or HOST.");
        if (request.Type is not ("PERCENT_OFF" or "FIXED_OFF"))
            return Error.Validation("Supported promotion types are PERCENT_OFF and FIXED_OFF.");
        if (request.Value <= 0)
            return Error.Validation("The discount value must be positive.");
        if (request.Type == "PERCENT_OFF" && request.Value > 100)
            return Error.Validation("A percent discount cannot exceed 100.");

        var effect = JsonSerializer.Serialize(new { value = request.Value });
        var conditions = JsonSerializer.Serialize(request.MinAmount is { } m ? new { min_amount = m } : (object)new { });

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO promotion.promotion (owner_type, owner_id, name, type, conditions, effect, valid_from, valid_to, budget, status)
            VALUES (@OwnerType, @OwnerId, @Name, @Type, CAST(@conditions AS jsonb), CAST(@effect AS jsonb), @ValidFrom, @ValidTo, @Budget, 'ACTIVE')
            RETURNING id
            """, new { request.OwnerType, request.OwnerId, request.Name, request.Type, conditions, effect, request.ValidFrom, request.ValidTo, request.Budget },
            cancellationToken: ct));

        return Result<PromotionResponse>.Success(new PromotionResponse(id, request.Name, request.Type, "ACTIVE"));
    }

    public async Task<Result<CouponResponse>> IssueCouponAsync(long promotionId, IssueCouponRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return Error.Validation("A coupon code is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM promotion.promotion WHERE id = @promotionId)", new { promotionId }, cancellationToken: ct));
        if (!exists)
            return Error.NotFound("promotion-not-found", $"Promotion {promotionId} was not found.");

        try
        {
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO promotion.coupon (promotion_id, code, max_redemptions, per_user_limit)
                VALUES (@promotionId, @Code, @MaxRedemptions, COALESCE(@PerUserLimit, 1))
                RETURNING id
                """, new { promotionId, request.Code, request.MaxRedemptions, request.PerUserLimit }, cancellationToken: ct));
            return Result<CouponResponse>.Success(new CouponResponse(id, request.Code, "ACTIVE"));
        }
        catch (PostgresException e) when (e.SqlState == "23505") // unique_violation on code
        {
            return Error.Conflict("coupon-code-taken", $"Coupon code '{request.Code}' is already in use.");
        }
    }

    /// <summary>Read-only preview: validates the coupon and computes the discount on <paramref name="amount"/>.</summary>
    public async Task<Result<DiscountQuote>> ApplyAsync(
        string code, decimal amount, string currency, DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var coupon = await LoadAsync(conn, null, code, ct);
        var validation = Validate(coupon, amount, asOf);
        if (validation is { } error)
            return error;

        var spent = await SpentAsync(conn, null, coupon!.PromotionId, ct);
        var discount = Discount(coupon, amount);
        if (coupon.Budget is { } budget && spent + discount > budget)
            return Error.Conflict("budget-exhausted", "This promotion's budget is exhausted.");

        return Result<DiscountQuote>.Success(new DiscountQuote(coupon.CouponId, discount, amount - discount, currency));
    }

    /// <summary>Confirm-time commit: records the redemption once per booking and advances the counters.</summary>
    public async Task<Result<DiscountQuote>> RedeemAsync(
        string code, long bookingId, long? guestId, decimal amount, string currency, DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var coupon = await LoadAsync(conn, tx, code, ct);
        if (coupon is null)
            return Error.NotFound("coupon-not-found", $"Coupon '{code}' was not found.");

        // Already redeemed for this booking → return the recorded amount unchanged (idempotent, BR-2).
        var existing = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            "SELECT amount FROM promotion.coupon_redemption WHERE coupon_id = @CouponId AND booking_id = @bookingId",
            new { coupon.CouponId, bookingId }, tx, cancellationToken: ct));
        if (existing is { } recorded)
        {
            await tx.CommitAsync(ct);
            return Result<DiscountQuote>.Success(new DiscountQuote(coupon.CouponId, recorded, amount - recorded, currency));
        }

        var validation = Validate(coupon, amount, asOf);
        if (validation is { } error)
            return error;

        var spent = await SpentAsync(conn, tx, coupon.PromotionId, ct);
        var discount = Discount(coupon, amount);
        if (coupon.Budget is { } budget && spent + discount > budget)
            return Error.Conflict("budget-exhausted", "This promotion's budget is exhausted.");

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO promotion.coupon_redemption (coupon_id, booking_id, guest_id, amount)
            VALUES (@CouponId, @bookingId, @guestId, @discount)
            """, new { coupon.CouponId, bookingId, guestId, discount }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE promotion.coupon SET redeemed_count = redeemed_count + 1 WHERE id = @CouponId",
            new { coupon.CouponId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<DiscountQuote>.Success(new DiscountQuote(coupon.CouponId, discount, amount - discount, currency));
    }

    private static async Task<CouponRow?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string code, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<CouponRow>(new CommandDefinition($"""
            SELECT c.id AS CouponId, c.status AS CouponStatus, c.max_redemptions AS MaxRedemptions, c.redeemed_count AS RedeemedCount,
                   p.id AS PromotionId, p.type AS Type, p.status AS PromotionStatus, p.valid_from AS ValidFrom, p.valid_to AS ValidTo,
                   p.budget AS Budget, (p.effect->>'value')::numeric AS EffectValue, (p.conditions->>'min_amount')::numeric AS MinAmount
            FROM promotion.coupon c JOIN promotion.promotion p ON p.id = c.promotion_id
            WHERE c.code = @code
            {(tx is null ? "" : "FOR UPDATE OF c")}
            """, new { code }, tx, cancellationToken: ct));

    private static Task<decimal> SpentAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long promotionId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(cr.amount), 0)
            FROM promotion.coupon_redemption cr JOIN promotion.coupon c ON c.id = cr.coupon_id
            WHERE c.promotion_id = @promotionId
            """, new { promotionId }, tx, cancellationToken: ct));

    private static Error? Validate(CouponRow? coupon, decimal amount, DateTimeOffset asOf)
    {
        if (coupon is null)
            return Error.NotFound("coupon-not-found", "The coupon was not found.");
        if (coupon.CouponStatus != "ACTIVE")
            return Error.Conflict("coupon-inactive", "This coupon is not active.");
        if (coupon.PromotionStatus != "ACTIVE")
            return Error.Conflict("promotion-inactive", "This promotion is not active.");
        if (coupon.ValidFrom is { } from && asOf.UtcDateTime < from)
            return Error.Conflict("outside-window", "This promotion has not started yet.");
        if (coupon.ValidTo is { } to && asOf.UtcDateTime > to)
            return Error.Conflict("outside-window", "This promotion has ended.");
        if (coupon.MinAmount is { } min && amount < min)
            return Error.Conflict("below-minimum", $"This coupon requires a minimum amount of {min}.");
        if (coupon.MaxRedemptions is { } max && coupon.RedeemedCount >= max)
            return Error.Conflict("max-redemptions-reached", "This coupon has reached its redemption limit.");
        return null;
    }

    private static decimal Discount(CouponRow coupon, decimal amount)
    {
        var raw = coupon.Type switch
        {
            "PERCENT_OFF" => Math.Round(amount * coupon.EffectValue / 100m, 2, MidpointRounding.AwayFromZero),
            "FIXED_OFF" => coupon.EffectValue,
            _ => 0m
        };
        return Math.Min(raw, amount); // never discount below zero
    }

    private sealed record CouponRow(
        long CouponId, string CouponStatus, int? MaxRedemptions, int RedeemedCount,
        long PromotionId, string Type, string PromotionStatus, DateTime? ValidFrom, DateTime? ValidTo,
        decimal? Budget, decimal EffectValue, decimal? MinAmount);
}
