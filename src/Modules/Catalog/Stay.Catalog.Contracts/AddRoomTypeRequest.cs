namespace Stay.Catalog.Contracts;

/// <summary>Body for <c>POST /api/v1/properties/{propertyId}/room-types</c>. Property comes from the route.</summary>
public sealed record AddRoomTypeRequest(
    string Name,
    string UnitKind,
    int TotalUnits,
    short BaseOccupancy,
    short MaxOccupancy,
    short? MaxAdults,
    short? MaxChildren,
    BedConfigDto? BedConfig,
    decimal? SizeSqm);

public sealed record BedConfigDto(int Doubles, int Singles, int Sofabeds);
