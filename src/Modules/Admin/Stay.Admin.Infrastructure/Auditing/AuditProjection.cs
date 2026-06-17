using Microsoft.EntityFrameworkCore;
using Stay.Admin.Domain.Auditing;
using Stay.Admin.Infrastructure.Persistence;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>
/// Base for audit projections: writes the mapped audit row plus an inbox marker in one transaction,
/// keyed by event id, so at-least-once redelivery never duplicates the entry (BR-5).
/// </summary>
public abstract class AuditProjection(AdminDbContext db) : IAuditProjection
{
    public abstract bool Handles(string eventType);

    /// <summary>Builds the audit row for the event, or null if it should be skipped.</summary>
    protected abstract AuditLogEntry? Map(OutboxEnvelope envelope);

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        if (await db.ProcessedEvents.AsNoTracking().AnyAsync(e => e.EventId == envelope.Id.ToString(), ct))
            return; // already recorded

        var entry = Map(envelope);
        if (entry is null)
            return;

        db.ProcessedEvents.Add(ProcessedEvent.Of(envelope.Id));
        db.AuditLog.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct); // audit row + inbox marker, one transaction
        }
        catch (DbUpdateException)
        {
            // Lost a race on the inbox PK → another delivery recorded it; safe to ignore.
        }
    }
}
