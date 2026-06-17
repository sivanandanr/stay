namespace Stay.BuildingBlocks.Cqrs;

/// <summary>A read request that does not mutate state and yields a <typeparamref name="TResponse"/>.</summary>
public interface IQuery<TResponse>;

/// <summary>Handles a single <typeparamref name="TQuery"/>. Returns <see cref="Result{T}"/> so misses
/// (e.g. not found / not owned) flow as expected failures, not exceptions.</summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken ct);
}

/// <summary>Dispatches a query to its handler (logging only — queries don't mutate, so no validation/transaction).</summary>
public interface IQueryDispatcher
{
    Task<Result<TResponse>> Send<TResponse>(IQuery<TResponse> query, CancellationToken ct = default);
}
