using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Stay.BuildingBlocks.Messaging;

namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Publishes outbox messages to Kafka. The producer is idempotent (<c>EnableIdempotence</c> +
/// <c>Acks.All</c>) so a broker-side retry can't duplicate a record; consumer-side dedupe by
/// event id covers the dispatcher republishing after a crash.
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaEventPublisher(IOptions<OutboxOptions> options)
    {
        var o = options.Value;
        _topic = o.Topic;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true
        }).Build();
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct = default)
    {
        var envelope = JsonSerializer.Serialize(
            new OutboxEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt));

        var headers = new Headers();
        headers.Add("event-id", Encoding.UTF8.GetBytes(message.Id.ToString()));
        headers.Add("event-type", Encoding.UTF8.GetBytes(message.Type));

        var kafkaMessage = new Message<string, string>
        {
            Key = message.Id.ToString(),
            Value = envelope,
            Headers = headers
        };

        await _producer.ProduceAsync(_topic, kafkaMessage, ct);
    }

    public void Dispose() => _producer.Dispose();
}
