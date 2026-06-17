using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// End-to-end proof of P0-A6: an event written in the same transaction as a DB change is reliably
/// dispatched to Kafka and consumed exactly-effectively-once.
/// </summary>
public sealed class OutboxRoundTripTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.6.1").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(OutboxTestInfra.Ddl);
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _kafka.DisposeAsync();
    }

    [Fact]
    public async Task Event_written_in_same_transaction_round_trips_and_is_processed_exactly_once()
    {
        var options = Options.Create(new OutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Topic = "stay.test.outbox",
            ConsumerGroup = $"test-{Guid.NewGuid():N}",
            Schemas = ["catalog"]
        });

        // --- ARRANGE: write a DB change AND the event in ONE transaction -----------------------
        var eventId = Guid.NewGuid();
        long amenityId;
        var writer = new OutboxWriter();
        await using (var conn = new NpgsqlConnection(options.Value.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            amenityId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO catalog.amenity (code, category, label) VALUES (@code, 'TEST', 'probe') RETURNING id",
                new { code = $"TEST_{eventId:N}" }, tx));

            var @event = new TestEvent(eventId, amenityId, DateTimeOffset.UtcNow);
            await writer.WriteAsync(conn, tx, "catalog", @event);

            await tx.CommitAsync();
        }

        // Both the state change and the event landed atomically; the event is still unprocessed.
        await using (var conn = new NpgsqlConnection(options.Value.ConnectionString))
        {
            await conn.OpenAsync();
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM catalog.amenity WHERE id = @amenityId", new { amenityId }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM catalog.outbox_message WHERE id = @eventId AND processed_at IS NULL",
                new { eventId }));
        }

        // --- ACT: start the consumer, then run the dispatcher --------------------------------
        var handler = new CapturingHandler();
        var consumer = new OutboxConsumer(options, new IdempotentReceiver(), handler,
            NullLogger<OutboxConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);

        using var publisher = new KafkaEventPublisher(options);
        var dispatcher = new OutboxDispatcher(options, publisher, NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.EnsureTopicAsync();

        var published = await dispatcher.DispatchPendingAsync();

        try
        {
            // --- ASSERT: published once, row marked processed, consumer received it ----------
            Assert.Equal(1, published);

            await using (var conn = new NpgsqlConnection(options.Value.ConnectionString))
            {
                await conn.OpenAsync();
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM catalog.outbox_message WHERE id = @eventId AND processed_at IS NOT NULL",
                    new { eventId }));
            }

            Assert.True(
                await OutboxTestInfra.WaitUntilAsync(
                    () => handler.Received.Contains(eventId), TimeSpan.FromSeconds(30)),
                "Consumer did not receive the dispatched event within the timeout.");

            // A second dispatch pass publishes nothing — the row is already processed.
            Assert.Equal(0, await dispatcher.DispatchPendingAsync());

            // Redelivering the SAME envelope is absorbed: the consumer dedupes by event id.
            await ProduceRawAsync(options.Value, eventId);
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(1, handler.Received.Count(id => id == eventId));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Publishes a duplicate envelope directly to the topic to exercise consumer idempotency.</summary>
    private static async Task ProduceRawAsync(OutboxOptions options, Guid eventId)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.BootstrapServers }).Build();

        var envelope = System.Text.Json.JsonSerializer.Serialize(
            new OutboxEnvelope(eventId, "stay.catalog.test-event", "{}", DateTimeOffset.UtcNow));

        await producer.ProduceAsync(options.Topic,
            new Message<string, string> { Key = eventId.ToString(), Value = envelope });
        producer.Flush(TimeSpan.FromSeconds(5));
    }
}
