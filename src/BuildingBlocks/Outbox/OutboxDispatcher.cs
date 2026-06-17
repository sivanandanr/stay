using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Stay.BuildingBlocks.Messaging;

namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Polls each configured context's <c>outbox_message</c> table and reliably publishes pending
/// events to Kafka. It publishes BEFORE marking a row processed and commits both within one
/// transaction, so a crash between publish and commit republishes the row on restart
/// (at-least-once); the consumer dedupes by event id to make it effectively-once (BR-5).
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly OutboxOptions _options;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(IOptions<OutboxOptions> options, IEventPublisher publisher, ILogger<OutboxDispatcher> logger)
    {
        _options = options.Value;
        _publisher = publisher;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureTopicAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Don't fail app startup if the broker is briefly unreachable — the dispatch loop retries.
            _logger.LogWarning(ex, "Could not ensure the outbox topic at startup; will retry on dispatch.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await DispatchPendingAsync(stoppingToken);
                if (published == 0)
                    await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatch pass failed; retrying after poll interval.");
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>Drains every configured schema once and returns the number of messages published.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var schema in _options.Schemas)
            total += await DrainSchemaAsync(schema, ct);
        return total;
    }

    private async Task<int> DrainSchemaAsync(string schema, CancellationToken ct)
    {
        SchemaName.Validate(schema);

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // FOR UPDATE SKIP LOCKED lets multiple dispatcher instances run without double-publishing.
        var rows = (await conn.QueryAsync<OutboxRow>(new CommandDefinition($"""
            SELECT id AS Id, type AS Type, payload AS Payload, occurred_at AS OccurredAt
            FROM {schema}.outbox_message
            WHERE processed_at IS NULL
            ORDER BY occurred_at
            FOR UPDATE SKIP LOCKED
            LIMIT @BatchSize
            """, new { _options.BatchSize }, tx, cancellationToken: ct))).AsList();

        if (rows.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        foreach (var row in rows)
        {
            // Npgsql returns timestamptz as a UTC DateTime; carry it as an offset for the envelope.
            var occurredAt = new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc));
            await _publisher.PublishAsync(
                new OutboxMessage(row.Id, row.Type, row.Payload, occurredAt, null), ct);

            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE {schema}.outbox_message SET processed_at = now() WHERE id = @Id",
                new { row.Id }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Outbox dispatched {Count} message(s) from schema {Schema}.", rows.Count, schema);
        return rows.Count;
    }

    /// <summary>
    /// Creates the topic if it does not exist (the dev/prod Kafka has auto-create disabled).
    /// Safe to call repeatedly — an existing topic is treated as success.
    /// </summary>
    public async Task EnsureTopicAsync(CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync([
                new TopicSpecification { Name = _options.Topic, NumPartitions = 1, ReplicationFactor = 1 }
            ]);
            _logger.LogInformation("Created Kafka topic {Topic}.", _options.Topic);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Already present — nothing to do.
        }
    }

    private sealed record OutboxRow(Guid Id, string Type, string Payload, DateTime OccurredAt);
}
