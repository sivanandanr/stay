using Dapper;
using Npgsql;
using Stay.Admin.Infrastructure.Partners;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 9 — partner registry &amp; pricing: registration is audited (§10), pricing applies the partner's
/// markup + commission, and a suspended partner is refused.
/// </summary>
public sealed class PartnerTests : IAsyncLifetime
{
    private const string Actor = "ops|1";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private PartnerService _partners = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AdminSchema.Ddl);
        _partners = new PartnerService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<int> AuditCountAsync(string clientId)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM admin.audit_log WHERE action = 'partner.register' AND after->>'ClientId' = @clientId",
            new { clientId });
    }

    [Fact]
    public async Task Registering_a_partner_audits_the_action()
    {
        var result = await _partners.RegisterAsync(Actor, new RegisterPartnerRequest("Acme OTA", "acme", 12m, 8m));

        Assert.True(result.IsSuccess);
        Assert.Equal("ACTIVE", result.Value!.Status);
        Assert.Equal(1, await AuditCountAsync("acme"));
    }

    [Fact]
    public async Task A_duplicate_client_id_is_rejected()
    {
        await _partners.RegisterAsync(Actor, new RegisterPartnerRequest("Acme", "acme", 10m, 5m));

        var second = await _partners.RegisterAsync(Actor, new RegisterPartnerRequest("Acme2", "acme", 10m, 5m));

        Assert.False(second.IsSuccess);
        Assert.Equal("client-id-taken", second.Error!.Value.Code);
    }

    [Fact]
    public async Task Pricing_applies_markup_and_commission()
    {
        await _partners.RegisterAsync(Actor, new RegisterPartnerRequest("Acme", "acme", CommissionPct: 12m, MarkupPct: 8m));

        var quote = (await _partners.PriceAsync("acme", 1000.00m, "INR")).Value!;

        Assert.Equal(80.00m, quote.MarkupAmount);     // 8% markup
        Assert.Equal(1080.00m, quote.SellPrice);      // shown to the guest
        Assert.Equal(120.00m, quote.Commission);      // 12% commission
        Assert.Equal(880.00m, quote.NetToPlatform);   // base - commission
    }

    [Fact]
    public async Task A_suspended_partner_cannot_price()
    {
        await _partners.RegisterAsync(Actor, new RegisterPartnerRequest("Acme", "acme", 10m, 5m));
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE admin.partner SET status = 'SUSPENDED' WHERE client_id = 'acme'");
        }

        var result = await _partners.PriceAsync("acme", 1000.00m, "INR");

        Assert.False(result.IsSuccess);
        Assert.Equal("partner-suspended", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Pricing_for_an_unknown_client_is_not_found()
    {
        var result = await _partners.PriceAsync("ghost", 1000.00m, "INR");

        Assert.False(result.IsSuccess);
        Assert.Equal("partner-not-found", result.Error!.Value.Code);
    }
}
