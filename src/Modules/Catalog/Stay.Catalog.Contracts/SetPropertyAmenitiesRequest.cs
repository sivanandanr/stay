namespace Stay.Catalog.Contracts;

/// <summary>
/// Body for <c>PUT /api/v1/properties/{id}/amenities</c>. Replaces the property's amenity set with the
/// given amenity <c>codes</c> (the full desired set, not a delta). The owner comes from the token.
/// </summary>
public sealed record SetPropertyAmenitiesRequest(IReadOnlyList<string> Amenities);
