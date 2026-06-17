using Dapper;
using Npgsql;
using Stay.Guest.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>First-login guest provisioning against real Postgres: idempotent and race-safe (P0-B4, BR-5).</summary>
public sealed class GuestProvisioningTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private GuestProvisioningService _guests = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(GuestSchema.Ddl);
        _guests = new GuestProvisioningService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<int> ProfileCountAsync(string sub)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM guest.guest_profile WHERE identity_sub = @sub", new { sub });
    }

    [Fact]
    public async Task First_login_creates_a_profile_from_the_claims()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";

        var profile = await _guests.ProvisionAsync(sub, "guest@example.com", "Guest", emailVerified: true);

        Assert.True(profile.GuestId > 0);
        Assert.Equal("guest@example.com", profile.Email);
        Assert.True(profile.EmailVerified);
    }

    [Fact]
    public async Task Replay_returns_the_same_profile()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";

        var first = await _guests.ProvisionAsync(sub, "g@example.com", "G", true);
        var second = await _guests.ProvisionAsync(sub, "g@example.com", "G", true);

        Assert.Equal(first.GuestId, second.GuestId);
        Assert.Equal(1, await ProfileCountAsync(sub));
    }

    [Fact]
    public async Task Ten_concurrent_first_logins_create_exactly_one_profile()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _guests.ProvisionAsync(sub, "g@example.com", "G", true)));

        Assert.Single(results.Select(r => r.GuestId).Distinct());
        Assert.Equal(1, await ProfileCountAsync(sub));
    }
}
