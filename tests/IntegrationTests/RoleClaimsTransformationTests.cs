using System.Security.Claims;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Stay.Admin.Infrastructure.Roles;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / §12 — the server authorizes from admin.role_assignment: granted roles become role claims,
/// so a token without an "ops" role still passes ops checks once granted, and ungranted subjects don't.
/// </summary>
public sealed class RoleClaimsTransformationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private RoleService _roles = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AdminSchema.Ddl);
        _roles = new RoleService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private RoleClaimsTransformation NewTransformation() =>
        new(_roles, new MemoryCache(new MemoryCacheOptions()));

    private static ClaimsPrincipal AuthenticatedAs(string sub) =>
        new(new ClaimsIdentity([new Claim("sub", sub)], authenticationType: "Test"));

    [Fact]
    public async Task A_granted_role_becomes_a_role_claim()
    {
        await _roles.GrantAsync("admin|root", new RoleGrantRequest("user|9", "ops", null, null));

        var principal = await NewTransformation().TransformAsync(AuthenticatedAs("user|9"));

        Assert.True(principal.IsInRole("ops"));
    }

    [Fact]
    public async Task A_subject_with_no_grants_gets_no_roles()
    {
        var principal = await NewTransformation().TransformAsync(AuthenticatedAs("user|stranger"));

        Assert.False(principal.IsInRole("ops"));
    }

    [Fact]
    public async Task Transforming_twice_does_not_duplicate_role_claims()
    {
        await _roles.GrantAsync("admin|root", new RoleGrantRequest("user|9", "finance", null, null));
        var transformation = NewTransformation();

        var principal = await transformation.TransformAsync(AuthenticatedAs("user|9"));
        principal = await transformation.TransformAsync(principal); // re-run on the same principal

        Assert.True(principal.IsInRole("finance"));
        Assert.Equal(1, principal.Claims.Count(c => c is { Type: ClaimTypes.Role, Value: "finance" }));
    }

    [Fact]
    public async Task An_unauthenticated_principal_is_left_untouched()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // no auth type → not authenticated

        var result = await NewTransformation().TransformAsync(anonymous);

        Assert.False(result.Identity!.IsAuthenticated);
        Assert.Empty(result.Claims);
    }
}
