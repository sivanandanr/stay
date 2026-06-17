using FluentValidation;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Hosts.ApproveHost;

/// <summary>Admin action: approve a host so it may list. <see cref="ActorSub"/> is the admin's token subject (audited).</summary>
public sealed record ApproveHostCommand(string ActorSub, long HostId) : ICommand<HostResponse>;

public sealed class ApproveHostValidator : AbstractValidator<ApproveHostCommand>
{
    public ApproveHostValidator()
    {
        RuleFor(x => x.ActorSub).NotEmpty();
        RuleFor(x => x.HostId).GreaterThan(0);
    }
}
