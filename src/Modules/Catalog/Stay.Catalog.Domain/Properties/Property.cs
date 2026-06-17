using NetTopologySuite.Geometries;

namespace Stay.Catalog.Domain.Properties;

/// <summary>
/// A bookable property (hotel / villa / apartment …). Aggregate root mapped to
/// <c>catalog.property</c>. New properties start in <see cref="PropertyStatus.Draft"/> and only
/// go LIVE after moderation (CLAUDE.md §13, Phase 1).
/// </summary>
public sealed class Property
{
    private Property() { } // EF materialization

    public long Id { get; private set; }
    public long HostId { get; private set; }
    public string Name { get; private set; } = null!;
    public PropertyType Type { get; private set; }
    public string? Description { get; private set; }
    public short? StarRating { get; private set; }
    public PropertyStatus Status { get; private set; }

    /// <summary>WGS-84 point (SRID 4326) backing the <c>geo</c> geography column.</summary>
    public Point Geo { get; private set; } = null!;

    public string CountryCode { get; private set; } = null!;
    public long CityId { get; private set; }
    public Address Address { get; private set; } = null!;
    public string DefaultCurrency { get; private set; } = null!;

    /// <summary>IANA timezone; drives stay-night / cancellation math (BR-4).</summary>
    public string Timezone { get; private set; } = null!;

    public TimeOnly? CheckInTime { get; private set; }
    public TimeOnly? CheckOutTime { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int RowVersion { get; private set; }

    /// <summary>
    /// Creates a new property owned by <paramref name="hostId"/>, in DRAFT. Caller is responsible
    /// for authorizing the host and for validating inputs (the command validator); this factory
    /// guards the invariants the type itself owns.
    /// </summary>
    public static Property Create(
        long hostId,
        string name,
        PropertyType type,
        string? description,
        short? starRating,
        double latitude,
        double longitude,
        string countryCode,
        long cityId,
        Address address,
        string defaultCurrency,
        string timezone,
        TimeOnly? checkInTime,
        TimeOnly? checkOutTime)
    {
        return new Property
        {
            HostId = hostId,
            Name = name.Trim(),
            Type = type,
            Description = description,
            StarRating = starRating,
            Status = PropertyStatus.Draft,
            Geo = new Point(longitude, latitude) { SRID = 4326 },
            CountryCode = countryCode.ToUpperInvariant(),
            CityId = cityId,
            Address = address,
            DefaultCurrency = defaultCurrency.ToUpperInvariant(),
            Timezone = timezone,
            CheckInTime = checkInTime,
            CheckOutTime = checkOutTime
        };
    }

    /// <summary>Owner submits a draft for moderation (DRAFT → IN_REVIEW).</summary>
    public void SubmitForReview() => Status = PropertyStatus.InReview;

    /// <summary>Moderator approves a property to go live (IN_REVIEW → LIVE).</summary>
    public void Publish() => Status = PropertyStatus.Live;

    /// <summary>Moderator sends a property back for changes (IN_REVIEW → DRAFT).</summary>
    public void ReturnToDraft() => Status = PropertyStatus.Draft;
}
