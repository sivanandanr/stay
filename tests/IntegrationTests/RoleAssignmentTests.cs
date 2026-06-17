using Dapper;
using Npgsql;
using Stay.Admin.Infrastructure.Roles;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / §12 — platform roles live in admin.role_assignment (server-side authz source), and every
/// grant/revoke is audited in the same transaction (§10), idempotently.
/// </summary>
public sealed class RoleAssignmentTests : IAsyncLifetime
{
    private const string Actor = "admin|root";
    private const string Subject = "user|42";

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

    private async Task<(int Assignments, int Audits)> CountsAsync(string action)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var assignments = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM admin.role_assignment WHERE identity_sub = @Subject", new { Subject });
        var audits = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM admin.audit_log WHERE entity_id = @Subject AND action = @action", new { Subject, action });
        return (assignments, audits);
    }

    [Fact]
    public async Task Granting_a_role_assigns_it_and_audits_once()
    {
        var result = await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "ops", null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal((1, 1), await CountsAsync("role.grant"));

        var roles = await _roles.GetRolesAsync(Subject);
        Assert.Contains(roles, r => r.RoleCode == "ops");
    }

    [Fact]
    public async Task Granting_the_same_role_twice_is_idempotent_and_audits_once()
    {
        await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "ops", null, null));
        await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "ops", null, null));

        Assert.Equal((1, 1), await CountsAsync("role.grant")); // one assignment, one audit
    }

    [Fact]
    public async Task Property_scoped_and_global_grants_of_the_same_role_coexist()
    {
        await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "host", null, null));
        await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "host", "PROPERTY", 100));

        Assert.Equal(2, (await CountsAsync("role.grant")).Assignments);
    }

    [Fact]
    public async Task Revoking_removes_the_assignment_and_audits()
    {
        await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "finance", null, null));

        var revoked = await _roles.RevokeAsync(Actor, new RoleGrantRequest(Subject, "finance", null, null));

        Assert.True(revoked.Value);
        Assert.Equal(0, (await CountsAsync("role.revoke")).Assignments);
        Assert.Equal(1, (await CountsAsync("role.revoke")).Audits);
    }

    [Fact]
    public async Task Revoking_a_missing_role_is_a_no_op_and_not_audited()
    {
        var revoked = await _roles.RevokeAsync(Actor, new RoleGrantRequest(Subject, "ops", null, null));

        Assert.False(revoked.Value);
        Assert.Equal(0, (await CountsAsync("role.revoke")).Audits);
    }

    [Fact]
    public async Task Granting_an_unknown_role_is_not_found()
    {
        var result = await _roles.GrantAsync(Actor, new RoleGrantRequest(Subject, "wizard", null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("role-not-found", result.Error!.Value.Code);
    }
}
