using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Admin.Infrastructure.Auditing;

/// <summary>
/// The admin context's own consumer of the outbox topic (separate consumer group, so it commits
/// offsets independently of other consumers). Routes host approval/rejection events to the audit
/// projection. This is the platform's cross-context, event-driven integration: Catalog decides,
/// Admin records — no cross-context table access. Offsets are committed only after a successful
/// projection, so a failure is retried on restart (the projection is idempotent, BR-5).
/// </summary>
public sealed class AdminAuditConsumer(
    IOptions<OutboxOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<AdminAuditConsumer> logger) : BackgroundService
{
    private const string GroupId = "stay-admin-audit";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    private void ConsumeLoop(CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = o.BootstrapServers,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

        consumer.Subscribe(o.Topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Admin audit consume error; continuing.");
                    continue;
                }

                if (result?.Message is null)
                    continue;

                try
                {
                    ProjectAsync(result.Message.Value, stoppingToken).GetAwaiter().GetResult();
                    consumer.Commit(result); // advance only after a successful projection
                }
                catch (Exception ex)
                {
                    // Leave the offset uncommitted so the message is retried on restart (idempotent).
                    logger.LogError(ex, "Failed to project audit event; offset left uncommitted.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProjectAsync(string value, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<OutboxEnvelope>(value);
        if (envelope is null)
            return; // malformed — nothing to record, safe to commit

        using var scope = scopeFactory.CreateScope();
        var projections = scope.ServiceProvider.GetServices<IAuditProjection>()
            .Where(p => p.Handles(envelope.Type));

        foreach (var projection in projections)
            await projection.ProjectAsync(envelope, ct);
    }
}
