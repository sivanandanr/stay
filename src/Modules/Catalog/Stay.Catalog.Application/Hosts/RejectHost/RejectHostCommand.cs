using FluentValidation;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Hosts.RejectHost;

/// <summary>Admin action: reject a host (→ SUSPENDED). A reason is mandatory (CLAUDE.md §10).</summary>
public sealed record RejectHostCommand(string ActorSub, long HostId, string Reason) : ICommand<HostResponse>;

public sealed class RejectHostValidator : AbstractValidator<RejectHostCommand>
{
    public RejectHostValidator()
    {
        RuleFor(x => x.ActorSub).NotEmpty();
        RuleFor(x => x.HostId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
