using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Search.Infrastructure;

/// <summary>
/// The search context's consumer of the outbox topic (own group, manual commit after success). Routes
/// catalog property events to the index projection. The projection is idempotent (upsert by id), so
/// the search read model stays eventually consistent with the catalog (§4).
/// </summary>
public sealed class SearchIndexerConsumer(
    IOptions<OutboxOptions> options,
    SearchIndexProjection projection,
    PopularityProjection popularity,
    RatingProjection rating,
    SearchPriceProjection price,
    SearchAmenitiesProjection amenities,
    ILogger<SearchIndexerConsumer> logger) : BackgroundService
{
    private const string GroupId = "stay-search-indexer";

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
                    logger.LogWarning(ex, "Search indexer consume error; continuing.");
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
                    logger.LogError(ex, "Failed to index event; offset left uncommitted for retry.");
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
            return;

        if (SearchIndexProjection.Handles(envelope.Type))
            await projection.ProjectAsync(envelope, ct);
        else if (PopularityProjection.Handles(envelope.Type))
            await popularity.ProjectAsync(envelope, ct);
        else if (RatingProjection.Handles(envelope.Type))
            await rating.ProjectAsync(envelope, ct);
        else if (SearchPriceProjection.Handles(envelope.Type))
            await price.ProjectAsync(envelope, ct);
        else if (SearchAmenitiesProjection.Handles(envelope.Type))
            await amenities.ProjectAsync(envelope, ct);
        // else: not ours — safe to commit
    }
}
