using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks.Messaging;

namespace Stay.BuildingBlocks.Outbox;

/// <inheritdoc cref="IOutboxWriter"/>
public sealed class OutboxWriter : IOutboxWriter
{
    public async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        IIntegrationEvent @event,
        CancellationToken ct = default)
    {
        SchemaName.Validate(schema);

        // Serialize against the concrete runtime type so all event properties are captured.
        var payload = JsonSerializer.Serialize(@event, @event.GetType());

        var sql = $"""
            INSERT INTO {schema}.outbox_message (id, type, payload)
            VALUES (@Id, @Type, CAST(@Payload AS jsonb))
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = @event.EventId, Type = @event.EventType, Payload = payload },
            transaction,
            cancellationToken: ct));
    }
}
