using FluentValidation;

namespace Stay.Catalog.Application.Properties.CreateProperty;

public sealed class CreatePropertyValidator : AbstractValidator<CreatePropertyCommand>
{
    public CreatePropertyValidator()
    {
        RuleFor(x => x.OwnerSub).NotEmpty();

        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        RuleFor(x => x.PropertyType)
            .Must(t => PropertyTypeMap.TryParse(t, out _))
            .WithMessage("PropertyType must be one of HOTEL, VILLA, APARTMENT, HOMESTAY, RESORT.");

        RuleFor(x => x.StarRating)
            .InclusiveBetween((short)1, (short)5)
            .When(x => x.StarRating is not null);

        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);

        RuleFor(x => x.CountryCode).NotEmpty().Length(2);
        RuleFor(x => x.CityId).GreaterThan(0);

        RuleFor(x => x.DefaultCurrency).NotEmpty().Length(3);
        RuleFor(x => x.Timezone).NotEmpty().MaximumLength(64);

        RuleFor(x => x.Address).NotNull();
        When(x => x.Address is not null, () =>
        {
            RuleFor(x => x.Address.Line1).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Address.City).NotEmpty().MaximumLength(120);
            RuleFor(x => x.Address.PostalCode).NotEmpty().MaximumLength(20);
            RuleFor(x => x.Address.CountryCode).NotEmpty().Length(2);
        });
    }
}
