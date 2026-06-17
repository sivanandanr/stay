using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stay.BuildingBlocks.Messaging;

namespace Stay.BuildingBlocks.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox writer, the Kafka publisher and the dispatcher hosted service.
    /// </summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services, Action<OutboxOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IOutboxWriter, OutboxWriter>();
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        return services;
    }

    /// <summary>
    /// Registers the demo round-trip consumer with the default logging handler. Call after
    /// <see cref="AddOutbox"/> (which configures <see cref="OutboxOptions"/>).
    /// </summary>
    public static IServiceCollection AddOutboxConsumer(this IServiceCollection services)
    {
        services.AddSingleton<IdempotentReceiver>();
        services.TryAddSingleton<IIntegrationEventHandler, LoggingIntegrationEventHandler>();
        services.AddHostedService<OutboxConsumer>();
        return services;
    }
}
