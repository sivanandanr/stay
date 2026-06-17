using Stay.BuildingBlocks.Outbox;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>
/// Projects a privileged-action integration event into <c>admin.audit_log</c>. Implementations are
/// idempotent (inbox-deduped by event id). The admin consumer routes each event to the projections
/// that declare they handle its type.
/// </summary>
public interface IAuditProjection
{
    bool Handles(string eventType);
    Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default);
}
