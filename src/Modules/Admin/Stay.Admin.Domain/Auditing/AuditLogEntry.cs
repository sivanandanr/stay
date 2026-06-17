namespace Stay.Admin.Domain.Auditing;

/// <summary>
/// One append-only row of <c>admin.audit_log</c> — business-event evidence of a privileged action
/// (CLAUDE.md §10). Never updated or deleted.
/// </summary>
public sealed class AuditLogEntry
{
    private AuditLogEntry() { } // EF materialization

    public long Id { get; private set; }
    public string ActorSub { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string EntityType { get; private set; } = null!;
    public string EntityId { get; private set; } = null!;
    public string? Before { get; private set; }   // jsonb
    public string? After { get; private set; }     // jsonb
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLogEntry Record(
        string actorSub, string action, string entityType, string entityId,
        string? before, string? after, string? reason) => new()
    {
        ActorSub = actorSub,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        Before = before,
        After = after,
        Reason = reason
    };
}
