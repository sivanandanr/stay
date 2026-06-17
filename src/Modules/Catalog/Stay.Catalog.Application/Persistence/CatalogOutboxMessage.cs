using System.Text.Json;
using Stay.BuildingBlocks.Messaging;

namespace Stay.Catalog.Application.Persistence;

/// <summary>
/// EF-mapped row of <c>catalog.outbox_message</c>. Written through the same DbContext (hence the
/// same transaction) as the state change, so the event and the change commit together — no
/// dual-write (BR-11). The dispatcher drains the table independently.
/// </summary>
public sealed class CatalogOutboxMessage
{
    public Guid Id { get; init; }
    public string Type { get; init; } = null!;
    public string Payload { get; init; } = null!;
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }

    public static CatalogOutboxMessage From(IIntegrationEvent @event) => new()
    {
        Id = @event.EventId,
        Type = @event.EventType,
        Payload = JsonSerializer.Serialize(@event, @event.GetType())
    };
}
