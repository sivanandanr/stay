using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Catalog.Application.Hosts.ApproveHost;
using Stay.Catalog.Application.Hosts.RegisterHost;
using Stay.Catalog.Application.Hosts.RejectHost;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Hosts;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Host approval/rejection against real Postgres: status flips with a durable audit event in one
/// transaction, idempotently; and the end-to-end gate — an approved host can then list.
/// </summary>
public sealed class HostApprovalTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgis/postgis:16-3.4").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(CatalogSchema.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private CatalogDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), o => o.UseNetTopologySuite())
            .Options;
        return new CatalogDbContext(options);
    }

    private async Task<long> SeedHostAsync(string status = "PENDING")
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO catalog.host (identity_sub, display_name, status)
            VALUES (@sub, 'Acme Stays', @status) RETURNING id
            """, new { sub, status });
    }

    private async Task<long> SeedCityAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO catalog.city (name, country_code, geo, timezone)
            VALUES ('Singapore', 'SG', ST_SetSRID(ST_MakePoint(103.85, 1.29), 4326)::geography, 'Asia/Singapore')
            RETURNING id
            """);
    }

    private static CreatePropertyCommand PropertyFor(string ownerSub, long cityId) => new(
        OwnerSub: ownerSub, Name: "Marina Bay Suites", PropertyType: "HOTEL", Description: null,
        StarRating: 5, Latitude: 1.2834, Longitude: 103.8607, CountryCode: "SG", CityId: cityId,
        Address: new AddressDto("1 Bayfront Ave", null, "Singapore", null, "018971", "SG"),
        DefaultCurrency: "SGD", Timezone: "Asia/Singapore", CheckInTime: null, CheckOutTime: null);

    [Fact]
    public async Task Approving_a_pending_host_makes_it_active_and_emits_one_event()
    {
        var hostId = await SeedHostAsync("PENDING");

        await using (var db = NewDbContext())
        {
            var result = await new ApproveHostHandler(db).Handle(new ApproveHostCommand("admin|1", hostId), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal("ACTIVE", result.Value!.Status);
        }

        // Re-approve is idempotent: still ACTIVE, still exactly one HostApproved event.
        await using (var db = NewDbContext())
            await new ApproveHostHandler(db).Handle(new ApproveHostCommand("admin|1", hostId), default);

        await using (var db = NewDbContext())
        {
            var host = await db.Hosts.SingleAsync(h => h.Id == hostId);
            Assert.Equal(HostStatus.Active, host.Status);
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.host-approved"));
        }
    }

    [Fact]
    public async Task Rejecting_a_host_suspends_it_and_records_the_reason()
    {
        var hostId = await SeedHostAsync("PENDING");

        await using (var db = NewDbContext())
        {
            var result = await new RejectHostHandler(db)
                .Handle(new RejectHostCommand("admin|1", hostId, "Incomplete KYC documents."), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal("SUSPENDED", result.Value!.Status);
        }

        await using (var db = NewDbContext())
        {
            var host = await db.Hosts.SingleAsync(h => h.Id == hostId);
            Assert.Equal(HostStatus.Suspended, host.Status);

            var outbox = await db.OutboxMessages.SingleAsync(m => m.Type == "stay.catalog.host-rejected");
            Assert.Contains("Incomplete KYC documents.", outbox.Payload); // reason captured in the audit event
        }
    }

    [Fact]
    public async Task Approving_a_missing_host_is_not_found()
    {
        await using var db = NewDbContext();
        var result = await new ApproveHostHandler(db).Handle(new ApproveHostCommand("admin|1", 999_999), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("host-not-found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Reject_requires_a_reason()
    {
        var valid = new RejectHostValidator().Validate(new RejectHostCommand("admin|1", 1, "ok"));
        var missing = new RejectHostValidator().Validate(new RejectHostCommand("admin|1", 1, ""));

        Assert.True(valid.IsValid);
        Assert.False(missing.IsValid);
    }

    [Fact]
    public async Task Approval_closes_the_gate_a_registered_host_cannot_list_until_approved()
    {
        var cityId = await SeedCityAsync();

        // Register (PENDING).
        string sub;
        long hostId;
        await using (var db = NewDbContext())
        {
            sub = $"auth0|{Guid.NewGuid():N}";
            hostId = (await new RegisterHostHandler(db).Handle(new RegisterHostCommand(sub, "Acme Stays"), default)).Value;
        }

        // Before approval: listing is blocked by the owner-approval gate.
        await using (var db = NewDbContext())
        {
            var blocked = await new CreatePropertyHandler(db).Handle(PropertyFor(sub, cityId), default);
            Assert.False(blocked.IsSuccess);
            Assert.Equal("owner-not-approved", blocked.Error!.Value.Code);
        }

        // Approve.
        await using (var db = NewDbContext())
            await new ApproveHostHandler(db).Handle(new ApproveHostCommand("admin|1", hostId), default);

        // After approval: listing succeeds.
        await using (var db = NewDbContext())
        {
            var allowed = await new CreatePropertyHandler(db).Handle(PropertyFor(sub, cityId), default);
            Assert.True(allowed.IsSuccess, allowed.Error?.Message);
        }
    }
}
