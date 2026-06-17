using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stay.BuildingBlocks.Cqrs;

/// <summary>Resolves the closed query handler at runtime and invokes it (with a log line either side).</summary>
public sealed class QueryDispatcher(IServiceProvider provider, ILogger<QueryDispatcher> logger)
    : IQueryDispatcher
{
    public async Task<Result<TResponse>> Send<TResponse>(IQuery<TResponse> query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var queryType = query.GetType();

        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(queryType, typeof(TResponse));
        dynamic handler = provider.GetRequiredService(handlerType);

        logger.LogInformation("Handling {Query}.", queryType.Name);
        Result<TResponse> result = await handler.Handle((dynamic)query, ct);
        logger.LogInformation("Handled {Query} → {Outcome}.",
            queryType.Name, result.IsSuccess ? "hit" : result.Error?.Code);

        return result;
    }
}
