using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stay.BuildingBlocks.Cqrs;

/// <summary>
/// Runs every command through a fixed pipeline: FluentValidation → logging → handler. The
/// handler's own <c>SaveChangesAsync</c> is the transaction boundary (one aggregate, one commit).
/// </summary>
public sealed class CommandDispatcher(IServiceProvider provider, ILogger<CommandDispatcher> logger)
    : ICommandDispatcher
{
    public async Task<Result<TResponse>> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandType = command.GetType();

        // 1. Validation — collect all failures across all registered validators for this command.
        var validators = provider
            .GetServices(typeof(IValidator<>).MakeGenericType(commandType))
            .Cast<IValidator>()
            .ToList();

        if (validators.Count > 0)
        {
            var context = new ValidationContext<object>(command);
            var failures = new List<FluentValidation.Results.ValidationFailure>();
            foreach (var validator in validators)
                failures.AddRange((await validator.ValidateAsync(context, ct)).Errors);

            if (failures.Count > 0)
            {
                var details = failures
                    .GroupBy(f => f.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
                logger.LogInformation("{Command} rejected by validation ({Count} error(s)).",
                    commandType.Name, failures.Count);
                return Error.Validation("One or more validation errors occurred.", details);
            }
        }

        // 2. Logging + 3. Handler dispatch (resolve the closed handler at runtime).
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(commandType, typeof(TResponse));
        dynamic handler = provider.GetRequiredService(handlerType);

        logger.LogInformation("Handling {Command}.", commandType.Name);
        Result<TResponse> result = await handler.Handle((dynamic)command, ct);
        logger.LogInformation("Handled {Command} → {Outcome}.",
            commandType.Name, result.IsSuccess ? "success" : result.Error?.Code);

        return result;
    }
}
