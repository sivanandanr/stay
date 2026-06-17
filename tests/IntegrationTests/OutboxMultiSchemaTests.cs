using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The dispatcher must drain EVERY writing context's outbox, not just catalog — otherwise booking /
/// payment / ari events never reach Kafka and their consumers (reviews, etc.) are dead. Uses a fake
/// publisher so no broker is needed.
/// </summary>
public sealed class OutboxMultiSchemaTests : IAsyncLifetime
{
    private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
        CREATE SCHEMA IF NOT EXISTS catalog;
        CREATE SCHEMA IF NOT EXISTS ari;
        CREATE SCHEMA IF NOT EXISTS booking;
        CREATE SCHEMA IF NOT EXISTS payment;
        CREATE TABLE catalog.outbox_message (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), type TEXT NOT NULL, payload JSONB NOT NULL, occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ);
        CREATE TABLE ari.outbox_message (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), type TEXT NOT NULL, payload JSONB NOT NULL, occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ);
        CREATE TABLE booking.outbox_message (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), type TEXT NOT NULL, payload JSONB NOT NULL, occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ);
        CREATE TABLE payment.outbox_message (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), type TEXT NOT NULL, payload JSONB NOT NULL, occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ);
        """;

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedOutboxAsync(string schema, string type)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            $"INSERT INTO {schema}.outbox_message (type, payload) VALUES (@type, CAST('{{}}' AS jsonb))", new { type });
    }

    private async Task<int> UnprocessedAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {schema}.outbox_message WHERE processed_at IS NULL");
    }

    [Fact]
    public async Task Dispatcher_drains_every_configured_schema()
    {
        await SeedOutboxAsync("catalog", "stay.catalog.x");
        await SeedOutboxAsync("booking", "stay.booking.x");
        await SeedOutboxAsync("payment", "stay.payment.x");

        var publisher = new CapturingPublisher();
        var dispatcher = new OutboxDispatcher(
            Options.Create(new OutboxOptions
            {
                ConnectionString = _postgres.GetConnectionString(),
                Schemas = ["catalog", "ari", "booking", "payment"] // ari has no pending rows → drains 0
            }),
            publisher, NullLogger<OutboxDispatcher>.Instance);

        var published = await dispatcher.DispatchPendingAsync();

        Assert.Equal(3, published);                       // all three schemas drained
        Assert.Equal(3, publisher.Published.Count);
        Assert.Contains(publisher.Published, m => m.Type == "stay.booking.x"); // booking events now flow
        Assert.Equal(0, await UnprocessedAsync("catalog"));
        Assert.Equal(0, await UnprocessedAsync("booking"));
        Assert.Equal(0, await UnprocessedAsync("payment"));
    }

    private sealed class CapturingPublisher : IEventPublisher
    {
        public List<OutboxMessage> Published { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken ct = default)
        {
            Published.Add(message);
            return Task.CompletedTask;
        }
    }
}
