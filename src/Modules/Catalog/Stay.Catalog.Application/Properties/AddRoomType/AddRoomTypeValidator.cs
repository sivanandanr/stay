using FluentValidation;

namespace Stay.Catalog.Application.Properties.AddRoomType;

public sealed class AddRoomTypeValidator : AbstractValidator<AddRoomTypeCommand>
{
    public AddRoomTypeValidator()
    {
        RuleFor(x => x.OwnerSub).NotEmpty();
        RuleFor(x => x.PropertyId).GreaterThan(0);

        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);

        RuleFor(x => x.UnitKind)
            .Must(k => UnitKindMap.TryParse(k, out _))
            .WithMessage("UnitKind must be one of ROOM, ENTIRE_UNIT.");

        RuleFor(x => x.TotalUnits).GreaterThanOrEqualTo(0);
        RuleFor(x => x.BaseOccupancy).GreaterThan((short)0);
        RuleFor(x => x.MaxOccupancy).GreaterThanOrEqualTo(x => x.BaseOccupancy)
            .WithMessage("MaxOccupancy must be at least BaseOccupancy.");

        RuleFor(x => x.MaxAdults).GreaterThan((short)0).When(x => x.MaxAdults is not null);
        RuleFor(x => x.MaxChildren).GreaterThanOrEqualTo((short)0).When(x => x.MaxChildren is not null);
        RuleFor(x => x.SizeSqm).GreaterThan(0).When(x => x.SizeSqm is not null);
    }
}
