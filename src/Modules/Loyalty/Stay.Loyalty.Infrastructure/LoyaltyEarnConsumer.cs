using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Loyalty.Infrastructure;

/// <summary>
/// The loyalty context's consumer of the outbox topic (own group, manual commit after success). Routes
/// BookingCompleted events to <see cref="LoyaltyEarnProjection"/>. The earn is idempotent by booking,
/// so the points balance stays eventually consistent under at-least-once delivery.
/// </summary>
public sealed class LoyaltyEarnConsumer(
    IOptions<OutboxOptions> options,
    LoyaltyEarnProjection projection,
    ILogger<LoyaltyEarnConsumer> logger) : BackgroundService
{
    private const string GroupId = "stay-loyalty";

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
                    logger.LogWarning(ex, "Loyalty consume error; continuing.");
                    continue;
                }

                if (result?.Message is null)
                    continue;

                try
                {
                    ProjectAsync(result.Message.Value, stoppingToken).GetAwaiter().GetResult();
                    consumer.Commit(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to earn loyalty points; offset left uncommitted for retry.");
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
        if (envelope is null || !LoyaltyEarnProjection.Handles(envelope.Type))
            return;

        await projection.ProjectAsync(envelope, ct);
    }
}
