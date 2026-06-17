using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;

namespace Stay.Admin.Infrastructure.Roles;

/// <summary>Request to grant/revoke a platform role to an identity, optionally scoped to a property.</summary>
public sealed record RoleGrantRequest(string IdentitySub, string RoleCode, string? ScopeType, long? ScopeId);

/// <summary>A role an identity holds.</summary>
public sealed record RoleAssignmentResponse(string RoleCode, string? ScopeType, long? ScopeId);

/// <summary>
/// Platform-role administration (CLAUDE.md §12: roles live in <c>admin.role_assignment</c>, not in the
/// identity provider; the server authorizes from them). Grants and revocations are privileged actions,
/// so each writes an <c>admin.audit_log</c> row in the SAME transaction as the role change (§10) — both
/// commit or neither. Idempotent: re-granting an existing assignment (or revoking a missing one) is a
/// no-op and is not re-audited. This is also the resolver the API uses to map a token <c>sub</c> to roles.
/// </summary>
public sealed class RoleService(string connectionString)
{
    public async Task<Result<RoleAssignmentResponse>> GrantAsync(
        string actorSub, RoleGrantRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdentitySub))
            return Error.Validation("An identity subject is required.");
        if (string.IsNullOrWhiteSpace(request.RoleCode))
            return Error.Validation("A role code is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var roleId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM admin.platform_role WHERE code = @RoleCode", new { request.RoleCode }, tx, cancellationToken: ct));
        if (roleId is null)
            return Error.NotFound("role-not-found", $"Unknown platform role '{request.RoleCode}'.");

        // Insert only if the (sub, role, scope) assignment is new — NULL-safe on the nullable scope.
        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO admin.role_assignment (identity_sub, role_id, scope_type, scope_id, granted_by)
            SELECT @IdentitySub, @roleId, @ScopeType, @ScopeId, @actorSub
            WHERE NOT EXISTS (
                SELECT 1 FROM admin.role_assignment
                WHERE identity_sub = @IdentitySub AND role_id = @roleId
                  AND scope_type IS NOT DISTINCT FROM @ScopeType
                  AND scope_id   IS NOT DISTINCT FROM @ScopeId)
            """, new { request.IdentitySub, roleId, request.ScopeType, request.ScopeId, actorSub }, tx, cancellationToken: ct));

        if (inserted == 1)
            await AuditAsync(conn, tx, actorSub, "role.grant", request, ct);

        await tx.CommitAsync(ct);
        return Result<RoleAssignmentResponse>.Success(
            new RoleAssignmentResponse(request.RoleCode, request.ScopeType, request.ScopeId));
    }

    public async Task<Result<bool>> RevokeAsync(
        string actorSub, RoleGrantRequest request, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var removed = await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM admin.role_assignment ra
            USING admin.platform_role pr
            WHERE ra.role_id = pr.id AND pr.code = @RoleCode AND ra.identity_sub = @IdentitySub
              AND ra.scope_type IS NOT DISTINCT FROM @ScopeType
              AND ra.scope_id   IS NOT DISTINCT FROM @ScopeId
            """, new { request.IdentitySub, request.RoleCode, request.ScopeType, request.ScopeId }, tx, cancellationToken: ct));

        if (removed >= 1)
            await AuditAsync(conn, tx, actorSub, "role.revoke", request, ct);

        await tx.CommitAsync(ct);
        return Result<bool>.Success(removed >= 1);
    }

    /// <summary>The role codes an identity holds — the authorization resolver (§12).</summary>
    public async Task<IReadOnlyList<RoleAssignmentResponse>> GetRolesAsync(string identitySub, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return (await conn.QueryAsync<RoleAssignmentResponse>(new CommandDefinition("""
            SELECT pr.code AS RoleCode, ra.scope_type AS ScopeType, ra.scope_id AS ScopeId
            FROM admin.role_assignment ra JOIN admin.platform_role pr ON pr.id = ra.role_id
            WHERE ra.identity_sub = @identitySub
            ORDER BY pr.code
            """, new { identitySub }, cancellationToken: ct))).AsList();
    }

    private static Task AuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string actorSub, string action, RoleGrantRequest request, CancellationToken ct)
    {
        var after = JsonSerializer.Serialize(new { request.RoleCode, request.ScopeType, request.ScopeId });
        return conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO admin.audit_log (actor_sub, action, entity_type, entity_id, after)
            VALUES (@actorSub, @action, 'role_assignment', @IdentitySub, CAST(@after AS jsonb))
            """, new { actorSub, action, request.IdentitySub, after }, tx, cancellationToken: ct));
    }
}
