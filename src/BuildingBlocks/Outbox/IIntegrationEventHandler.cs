using Microsoft.Extensions.Logging;

namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Handles an integration event after the consumer has confirmed it is not a duplicate.
/// The default implementation simply logs receipt (the P0-A6 round-trip proof); real consumers
/// register their own handler.
/// </summary>
public interface IIntegrationEventHandler
{
    void Handle(OutboxEnvelope envelope);
}

/// <summary>Default handler: logs that the event was received and de-duplicated.</summary>
public sealed class LoggingIntegrationEventHandler : IIntegrationEventHandler
{
    private readonly ILogger<LoggingIntegrationEventHandler> _logger;

    public LoggingIntegrationEventHandler(ILogger<LoggingIntegrationEventHandler> logger) => _logger = logger;

    public void Handle(OutboxEnvelope envelope) =>
        _logger.LogInformation(
            "Received integration event {EventId} of type {EventType} (occurred {OccurredAt:o}).",
            envelope.Id, envelope.Type, envelope.OccurredAt);
}
