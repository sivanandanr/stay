using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stay.BuildingBlocks.Cqrs;

public static class CqrsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dispatcher and scans the given assemblies for command handlers
    /// (<see cref="ICommandHandler{TCommand,TResponse}"/>) and FluentValidation validators.
    /// Safe to call from multiple modules — the dispatcher is registered once.
    /// </summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.TryAddScoped<ICommandDispatcher, CommandDispatcher>();
        services.TryAddScoped<IQueryDispatcher, QueryDispatcher>();

        foreach (var assembly in assemblies)
        {
            RegisterHandlers(services, assembly, typeof(ICommandHandler<,>));
            RegisterHandlers(services, assembly, typeof(IQueryHandler<,>));
            services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        }

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly, Type handlerInterface)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var @interface in type.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface))
            {
                services.AddScoped(@interface, type);
            }
        }
    }
}
