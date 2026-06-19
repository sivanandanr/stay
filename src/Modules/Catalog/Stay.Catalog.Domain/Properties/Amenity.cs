namespace Stay.Catalog.Domain.Properties;

/// <summary>
/// Platform-wide amenity master data (<c>catalog.amenity</c>) — WiFi, pool, parking, … Referenced by
/// properties and room types through join rows. Read-only from the app's perspective (seeded/managed
/// as reference data); the <see cref="Code"/> is the stable, searchable token.
/// </summary>
public sealed class Amenity
{
    private Amenity() { } // EF materialization

    public long Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Category { get; private set; } = null!;
    public string Label { get; private set; } = null!;
}
