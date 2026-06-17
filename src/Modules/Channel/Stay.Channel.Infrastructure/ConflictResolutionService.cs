using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Channel.Contracts;

namespace Stay.Channel.Infrastructure;

/// <summary>
/// Resolves (or escalates) a <c>channel.sync_conflict</c> — a privileged, audited action (§10). The
/// state change (OPEN → RESOLVED/ESCALATED) and the audit-evidence event commit in one transaction;
/// the Admin context records it in <c>admin.audit_log</c>. Idempotent: re-resolving a closed conflict
/// returns its current state without re-emitting. A resolution note is mandatory.
/// </summary>
public sealed class ConflictResolutionService(string connectionString)
{
    public async Task<Result<ConflictResolutionResponse>> ResolveAsync(
        long conflictId, string actorSub, string resolution, bool escalate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return Error.Validation("A resolution note is required.");

        var target = escalate ? "ESCALATED" : "RESOLVED";

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var conflict = await conn.QuerySingleOrDefaultAsync<ConflictRow>(new CommandDefinition("""
            SELECT id AS Id, property_id AS PropertyId, type AS Type, status AS Status, resolution AS Resolution
            FROM channel.sync_conflict WHERE id = @conflictId FOR UPDATE
            """, new { conflictId }, tx, cancellationToken: ct));

        if (conflict is null)
            return Error.NotFound("conflict-not-found", $"Sync conflict {conflictId} was not found.");
        if (conflict.Status != "OPEN")
            return Result<ConflictResolutionResponse>.Success(
                new ConflictResolutionResponse(conflict.Id, conflict.Status, conflict.Resolution ?? "")); // idempotent

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE channel.sync_conflict SET status = @target, resolution = @resolution WHERE id = @conflictId",
            new { target, resolution, conflictId }, tx, cancellationToken: ct));

        var @event = new ChannelConflictResolved(
            Guid.NewGuid(), conflictId, conflict.PropertyId, conflict.Type, actorSub, target, resolution, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO channel.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<ConflictResolutionResponse>.Success(new ConflictResolutionResponse(conflictId, target, resolution));
    }

    private sealed record ConflictRow(long Id, long PropertyId, string Type, string Status, string? Resolution);
}
