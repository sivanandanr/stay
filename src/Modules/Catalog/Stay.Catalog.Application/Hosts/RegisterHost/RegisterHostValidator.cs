using FluentValidation;

namespace Stay.Catalog.Application.Hosts.RegisterHost;

public sealed class RegisterHostValidator : AbstractValidator<RegisterHostCommand>
{
    public RegisterHostValidator()
    {
        RuleFor(x => x.OwnerSub).NotEmpty();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
    }
}
