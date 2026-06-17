using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Subscribes to the outbox topic and dispatches each message to the registered handler exactly
/// once per event id (<see cref="IdempotentReceiver"/>). Proves the producer → Kafka → consumer
/// round-trip for P0-A6, and that redeliveries are absorbed (BR-5).
/// </summary>
public sealed class OutboxConsumer : BackgroundService
{
    private readonly OutboxOptions _options;
    private readonly IdempotentReceiver _receiver;
    private readonly IIntegrationEventHandler _handler;
    private readonly ILogger<OutboxConsumer> _logger;

    public OutboxConsumer(
        IOptions<OutboxOptions> options,
        IdempotentReceiver receiver,
        IIntegrationEventHandler handler,
        ILogger<OutboxConsumer> logger)
    {
        _options = options.Value;
        _receiver = receiver;
        _handler = handler;
        _logger = logger;
    }

    // Consume() blocks; run the loop on its own thread so host startup isn't held up.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    private void ConsumeLoop(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        }).Build();

        consumer.Subscribe(_options.Topic);

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
                    _logger.LogWarning(ex, "Kafka consume error; continuing.");
                    continue;
                }

                if (result?.Message is null)
                    continue;

                Dispatch(result.Message.Value);
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

    private void Dispatch(string value)
    {
        OutboxEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OutboxEnvelope>(value);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Malformed outbox envelope; skipping.");
            return;
        }

        if (envelope is null)
            return;

        if (!_receiver.TryBegin(envelope.Id))
        {
            _logger.LogInformation(
                "Duplicate event {EventId} ({EventType}) ignored (idempotent consumer).",
                envelope.Id, envelope.Type);
            return;
        }

        _handler.Handle(envelope);
    }
}
