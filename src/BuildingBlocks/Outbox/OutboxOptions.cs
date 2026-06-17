namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Configuration for the outbox dispatcher, the Kafka publisher and the demo consumer.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Postgres connection string the dispatcher uses to drain outbox tables.</summary>
    public string ConnectionString { get; set; } = "";

    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Topic every outbox message is published to (keyed by event id).</summary>
    public string Topic { get; set; } = "stay.outbox";

    /// <summary>Consumer group for the demo round-trip consumer.</summary>
    public string ConsumerGroup { get; set; } = "stay-outbox-consumer";

    /// <summary>Schemas whose <c>outbox_message</c> tables the dispatcher drains each pass.</summary>
    public IList<string> Schemas { get; set; } = new List<string>();

    /// <summary>Idle delay between drain passes when nothing was pending.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Max rows claimed per schema per pass.</summary>
    public int BatchSize { get; set; } = 100;
}
