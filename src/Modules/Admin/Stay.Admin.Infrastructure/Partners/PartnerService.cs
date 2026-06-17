using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;

namespace Stay.Admin.Infrastructure.Partners;

/// <summary>Body for <c>POST /api/v1/admin/partners</c> — register a distribution partner (Phase 9).</summary>
public sealed record RegisterPartnerRequest(string Name, string ClientId, decimal CommissionPct, decimal MarkupPct);

/// <summary>A distribution partner.</summary>
public sealed record PartnerResponse(long Id, string Name, string ClientId, decimal CommissionPct, decimal MarkupPct, string Status);

/// <summary>
/// A partner's price view of a base amount: the sell price they show the guest (base + their markup)
/// and the platform's net after the partner's commission. Money is <c>NUMERIC</c>; each component
/// rounds half-away-from-zero to the minor unit.
/// </summary>
public sealed record PartnerPriceQuote(
    decimal BaseAmount, decimal MarkupAmount, decimal SellPrice, decimal Commission, decimal NetToPlatform, string Currency);

/// <summary>
/// Distribution-partner registry &amp; pricing (Phase 9 Partner API). Partners authenticate via OAuth
/// client-credentials; their <c>client_id</c> maps to an <c>admin.partner</c> row carrying commission and
/// markup. Registration is a privileged action, audited in the same transaction (§10). Pricing is a pure
/// computation off the partner's rates; a SUSPENDED partner is refused.
/// </summary>
public sealed class PartnerService(string connectionString)
{
    public async Task<Result<PartnerResponse>> RegisterAsync(string actorSub, RegisterPartnerRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Error.Validation("A partner name is required.");
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return Error.Validation("A partner client_id is required.");
        if (request.CommissionPct is < 0 or > 100 || request.MarkupPct is < 0 or > 100)
            return Error.Validation("Commission and markup percents must be between 0 and 100.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long id;
        try
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO admin.partner (name, client_id, commission_pct, markup_pct)
                VALUES (@Name, @ClientId, @CommissionPct, @MarkupPct)
                RETURNING id
                """, request, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            return Error.Conflict("client-id-taken", $"A partner with client_id '{request.ClientId}' already exists.");
        }

        var after = JsonSerializer.Serialize(new { request.Name, request.ClientId, request.CommissionPct, request.MarkupPct });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO admin.audit_log (actor_sub, action, entity_type, entity_id, after)
            VALUES (@actorSub, 'partner.register', 'partner', @id, CAST(@after AS jsonb))
            """, new { actorSub, id = id.ToString(), after }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<PartnerResponse>.Success(
            new PartnerResponse(id, request.Name, request.ClientId, request.CommissionPct, request.MarkupPct, "ACTIVE"));
    }

    public async Task<Result<PartnerPriceQuote>> PriceAsync(string clientId, decimal baseAmount, string currency, CancellationToken ct = default)
    {
        if (baseAmount < 0)
            return Error.Validation("The base amount cannot be negative.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var partner = await conn.QuerySingleOrDefaultAsync<PartnerRow>(new CommandDefinition("""
            SELECT commission_pct AS CommissionPct, markup_pct AS MarkupPct, status AS Status
            FROM admin.partner WHERE client_id = @clientId
            """, new { clientId }, cancellationToken: ct));

        if (partner is null)
            return Error.NotFound("partner-not-found", $"No partner is registered for client '{clientId}'.");
        if (partner.Status != "ACTIVE")
            return Error.Conflict("partner-suspended", "This partner account is suspended.");

        var markup = Math.Round(baseAmount * partner.MarkupPct / 100m, 2, MidpointRounding.AwayFromZero);
        var commission = Math.Round(baseAmount * partner.CommissionPct / 100m, 2, MidpointRounding.AwayFromZero);

        return Result<PartnerPriceQuote>.Success(new PartnerPriceQuote(
            baseAmount, markup, baseAmount + markup, commission, baseAmount - commission, currency));
    }

    private sealed record PartnerRow(decimal CommissionPct, decimal MarkupPct, string Status);
}
