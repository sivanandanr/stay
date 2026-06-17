using Npgsql;
using Stay.BuildingBlocks.Messaging;

namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Writes a domain event into a context's <c>outbox_message</c> table. The caller passes its
/// open connection and transaction so the event lands atomically with the state change —
/// no dual-write (Golden Rule, BR-11).
/// </summary>
public interface IOutboxWriter
{
    Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        IIntegrationEvent @event,
        CancellationToken ct = default);
}
