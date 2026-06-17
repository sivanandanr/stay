using System.Collections.Concurrent;
using Stay.BuildingBlocks.Outbox;

namespace Stay.IntegrationTests;

/// <summary>
/// Minimal slice of the catalog schema the outbox round-trip exercises (a state-change table +
/// the outbox table). Mirrors <c>db/schema.sql</c> so the writer/dispatcher run against the real
/// table shape without pulling in PostGIS or the full reference schema.
/// </summary>
internal static class OutboxTestInfra
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS catalog;

        CREATE TABLE catalog.amenity (
            id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            code      TEXT NOT NULL UNIQUE,
            category  TEXT NOT NULL,
            label     TEXT NOT NULL
        );

        CREATE TABLE catalog.outbox_message (
            id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            type         TEXT        NOT NULL,
            payload      JSONB       NOT NULL,
            occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            processed_at TIMESTAMPTZ
        );
        CREATE INDEX idx_catalog_outbox_unprocessed
            ON catalog.outbox_message (occurred_at) WHERE processed_at IS NULL;
        """;

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }
        return condition();
    }
}

/// <summary>Test handler that records every event id the consumer dispatches to it.</summary>
internal sealed class CapturingHandler : IIntegrationEventHandler
{
    public ConcurrentBag<Guid> Received { get; } = [];

    public void Handle(OutboxEnvelope envelope) => Received.Add(envelope.Id);
}
