namespace Stay.Catalog.Contracts;

/// <summary>Request body for <c>POST /api/v1/properties</c>. The owner is taken from the token, not the body.</summary>
public sealed record CreatePropertyRequest(
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
    TimeOnly? CheckOutTime);

public sealed record AddressDto(
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string PostalCode,
    string CountryCode);
