using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.CreateProperty;

/// <summary>
/// Creates a property for the authenticated owner. <see cref="OwnerSub"/> is the token subject the
/// endpoint resolved — never client-supplied — and is mapped to a host server-side.
/// </summary>
public sealed record CreatePropertyCommand(
    string OwnerSub,
    string Name,
    string PropertyType,
    string? Description,
    short? StarRating,
    double Latitude,
    double Longitude,
    string CountryCode,
    long CityId,
    AddressDto Address,
    string DefaultCurrency,
    string Timezone,
    TimeOnly? CheckInTime,
    TimeOnly? CheckOutTime) : ICommand<long>;
