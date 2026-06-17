using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Catalog.Application.Hosts.RegisterHost;
using Stay.Catalog.Domain.Hosts;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// RegisterHost against real Postgres: first-login provisioning is idempotent and race-safe
/// (BR-5, P0-B4) — N concurrent first-requests yield exactly one host and one event.
/// </summary>
public sealed class RegisterHostTests : IAsyncLifetime
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

    [Fact]
    public async Task First_registration_creates_a_pending_host_and_emits_HostRegistered()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";

        long hostId;
        await using (var db = NewDbContext())
        {
            var result = await new RegisterHostHandler(db).Handle(new RegisterHostCommand(sub, "Acme Stays"), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            hostId = result.Value;
        }

        await using (var db = NewDbContext())
        {
            var host = await db.Hosts.SingleAsync(h => h.Id == hostId);
            Assert.Equal(sub, host.IdentitySub);
            Assert.Equal(HostStatus.Pending, host.Status);          // starts pending approval
            Assert.False(host.CanList);

            var outbox = await db.OutboxMessages.SingleAsync();
            Assert.Equal("stay.catalog.host-registered", outbox.Type);
        }
    }

    [Fact]
    public async Task Replaying_registration_is_idempotent_and_emits_no_second_event()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        var command = new RegisterHostCommand(sub, "Acme Stays");

        long firstId, secondId;
        await using (var db = NewDbContext())
            firstId = (await new RegisterHostHandler(db).Handle(command, default)).Value;
        await using (var db = NewDbContext())
            secondId = (await new RegisterHostHandler(db).Handle(command, default)).Value;

        Assert.Equal(firstId, secondId); // same profile, not a duplicate

        await using var verify = NewDbContext();
        Assert.Equal(1, await verify.Hosts.CountAsync(h => h.IdentitySub == sub));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync()); // exactly one event, no replay emission
    }

    [Fact]
    public async Task Ten_concurrent_first_requests_create_exactly_one_host_and_one_event()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        var command = new RegisterHostCommand(sub, "Acme Stays");

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = NewDbContext();
            return await new RegisterHostHandler(db).Handle(command, default);
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Message));
        var distinctIds = results.Select(r => r.Value).Distinct().ToList();
        Assert.Single(distinctIds); // every caller resolved to the same host

        await using var verify = NewDbContext();
        Assert.Equal(1, await verify.Hosts.CountAsync(h => h.IdentitySub == sub));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync()); // only the winner emitted
    }
}
