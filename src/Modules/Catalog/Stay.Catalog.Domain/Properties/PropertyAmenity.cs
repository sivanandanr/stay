namespace Stay.Catalog.Domain.Properties;

/// <summary>
/// Join row linking a property to an <see cref="Amenity"/> (<c>catalog.property_amenity</c>). The full
/// set of a property's amenities is the collection of these rows; replacing the set is delete-all +
/// insert in one transaction.
/// </summary>
public sealed class PropertyAmenity
{
    private PropertyAmenity() { } // EF materialization

    public long PropertyId { get; private set; }
    public long AmenityId { get; private set; }

    public static PropertyAmenity Link(long propertyId, long amenityId) =>
        new() { PropertyId = propertyId, AmenityId = amenityId };
}
