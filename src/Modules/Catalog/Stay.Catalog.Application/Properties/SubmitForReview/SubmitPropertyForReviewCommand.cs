using FluentValidation;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.SubmitForReview;

/// <summary>Owner submits their drafted property for moderation. Owner-scoped by <see cref="OwnerSub"/>.</summary>
public sealed record SubmitPropertyForReviewCommand(string OwnerSub, long PropertyId)
    : ICommand<PropertyStatusResponse>;

public sealed class SubmitPropertyForReviewValidator : AbstractValidator<SubmitPropertyForReviewCommand>
{
    public SubmitPropertyForReviewValidator()
    {
        RuleFor(x => x.OwnerSub).NotEmpty();
        RuleFor(x => x.PropertyId).GreaterThan(0);
    }
}
