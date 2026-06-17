using FluentValidation;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.PublishProperty;

/// <summary>Moderator publishes a property (IN_REVIEW → LIVE). <see cref="ActorSub"/> is audited.</summary>
public sealed record PublishPropertyCommand(string ActorSub, long PropertyId) : ICommand<PropertyStatusResponse>;

public sealed class PublishPropertyValidator : AbstractValidator<PublishPropertyCommand>
{
    public PublishPropertyValidator()
    {
        RuleFor(x => x.ActorSub).NotEmpty();
        RuleFor(x => x.PropertyId).GreaterThan(0);
    }
}
