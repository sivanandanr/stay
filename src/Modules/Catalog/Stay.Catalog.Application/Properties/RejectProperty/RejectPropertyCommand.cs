using FluentValidation;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.RejectProperty;

/// <summary>Moderator rejects a property back to DRAFT (IN_REVIEW → DRAFT) with a mandatory reason.</summary>
public sealed record RejectPropertyCommand(string ActorSub, long PropertyId, string Reason)
    : ICommand<PropertyStatusResponse>;

public sealed class RejectPropertyValidator : AbstractValidator<RejectPropertyCommand>
{
    public RejectPropertyValidator()
    {
        RuleFor(x => x.ActorSub).NotEmpty();
        RuleFor(x => x.PropertyId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
