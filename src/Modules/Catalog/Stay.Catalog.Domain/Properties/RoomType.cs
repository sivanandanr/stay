namespace Stay.Catalog.Domain.Properties;

/// <summary>Mirrors the <c>unit_kind</c> CHECK on <c>catalog.room_type</c>.</summary>
public enum UnitKind
{
    Room,        // a sellable room within a property (hotel)
    EntireUnit   // the whole place (villa / apartment)
}

/// <summary>Optional bed layout, stored as <c>jsonb</c> on <c>catalog.room_type.bed_config</c>.</summary>
public sealed record BedConfig(int Doubles, int Singles, int Sofabeds);

/// <summary>
/// A sellable unit type within a property (mapped to <c>catalog.room_type</c>). Carries the
/// occupancy + inventory shape later consumed by ARI/pricing/booking.
/// </summary>
public sealed class RoomType
{
    private RoomType() { } // EF materialization

    public long Id { get; private set; }
    public long PropertyId { get; private set; }
    public string Name { get; private set; } = null!;
    public UnitKind UnitKind { get; private set; }
    public int TotalUnits { get; private set; }
    public short BaseOccupancy { get; private set; }
    public short MaxOccupancy { get; private set; }
    public short? MaxAdults { get; private set; }
    public short? MaxChildren { get; private set; }
    public BedConfig? BedConfig { get; private set; }
    public decimal? SizeSqm { get; private set; }
    public int RowVersion { get; private set; }

    /// <summary>
    /// Adds a room type to <paramref name="propertyId"/>. Guards the invariants the type owns;
    /// the command validator covers input shape, and the DB CHECKs are the backstop.
    /// </summary>
    public static RoomType Create(
        long propertyId,
        string name,
        UnitKind unitKind,
        int totalUnits,
        short baseOccupancy,
        short maxOccupancy,
        short? maxAdults,
        short? maxChildren,
        BedConfig? bedConfig,
        decimal? sizeSqm)
    {
        if (maxOccupancy < baseOccupancy)
            throw new ArgumentException("Max occupancy cannot be below base occupancy.", nameof(maxOccupancy));

        return new RoomType
        {
            PropertyId = propertyId,
            Name = name.Trim(),
            UnitKind = unitKind,
            TotalUnits = totalUnits,
            BaseOccupancy = baseOccupancy,
            MaxOccupancy = maxOccupancy,
            MaxAdults = maxAdults,
            MaxChildren = maxChildren,
            BedConfig = bedConfig,
            SizeSqm = sizeSqm
        };
    }
}
