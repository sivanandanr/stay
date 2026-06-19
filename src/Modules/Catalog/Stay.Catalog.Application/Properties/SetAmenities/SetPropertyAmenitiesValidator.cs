using FluentValidation;

namespace Stay.Catalog.Application.Properties.SetAmenities;

public sealed class SetPropertyAmenitiesValidator : AbstractValidator<SetPropertyAmenitiesCommand>
{
    public SetPropertyAmenitiesValidator()
    {
        RuleFor(x => x.OwnerSub).NotEmpty();
        RuleFor(x => x.PropertyId).GreaterThan(0);

        // An empty list is valid — it clears the property's amenities. Codes must be non-blank.
        RuleFor(x => x.AmenityCodes).NotNull();
        RuleForEach(x => x.AmenityCodes).NotEmpty().MaximumLength(60);
    }
}
