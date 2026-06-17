namespace Stay.BuildingBlocks.Cqrs;

/// <summary>A request that mutates state and yields a <typeparamref name="TResponse"/> on success.</summary>
public interface ICommand<TResponse>;

/// <summary>Handles a single <typeparamref name="TCommand"/>. Returns <see cref="Result{T}"/> for
/// expected failures; throws only for the genuinely exceptional (CLAUDE.md §5).</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct);
}

/// <summary>Entry point to the CQRS pipeline (validation → logging → handler).</summary>
public interface ICommandDispatcher
{
    Task<Result<TResponse>> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default);
}
