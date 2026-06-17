using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.AddRoomType;

/// <summary>
/// Adds a room type to a property the caller owns. <see cref="OwnerSub"/> is the token subject;
/// <see cref="PropertyId"/> comes from the route. Returns the new room-type id.
/// </summary>
public sealed record AddRoomTypeCommand(
    string OwnerSub,
    long PropertyId,
    string Name,
    string UnitKind,
    int TotalUnits,
    short BaseOccupancy,
    short MaxOccupancy,
    short? MaxAdults,
    short? MaxChildren,
    BedConfigDto? BedConfig,
    decimal? SizeSqm) : ICommand<long>;
