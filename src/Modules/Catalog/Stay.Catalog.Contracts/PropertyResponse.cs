namespace Stay.Catalog.Contracts;

/// <summary>A property as returned to its owner.</summary>
public sealed record PropertyResponse(
    long Id,
    long HostId,
    string Name,
    string PropertyType,
    string Status,
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
    TimeOnly? CheckOutTime,
    DateTimeOffset CreatedAt);
