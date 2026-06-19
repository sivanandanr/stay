using Stay.BuildingBlocks.Cqrs;

namespace Stay.Catalog.Application.Properties.SetAmenities;

/// <summary>
/// Replaces a property's amenity set with <see cref="AmenityCodes"/> (the full desired set).
/// <see cref="OwnerSub"/> is the token subject; <see cref="PropertyId"/> comes from the route. Returns
/// the count of amenities now linked.
/// </summary>
public sealed record SetPropertyAmenitiesCommand(
    string OwnerSub,
    long PropertyId,
    IReadOnlyList<string> AmenityCodes) : ICommand<int>;
