using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Stay.Catalog.Domain.Hosts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the catalog enums to the exact string values their <c>CHECK</c> constraints expect.
/// The converters wrap single method-call lambdas so the switch/throw logic stays out of the
/// (otherwise restrictive) EF expression trees.
/// </summary>
internal static class EnumConverters
{
    public static readonly ValueConverter<PropertyType, string> PropertyType =
        new(v => ToDb(v), v => FromPropertyType(v));

    public static readonly ValueConverter<PropertyStatus, string> PropertyStatus =
        new(v => ToDb(v), v => FromPropertyStatus(v));

    public static readonly ValueConverter<HostStatus, string> HostStatus =
        new(v => ToDb(v), v => FromHostStatus(v));

    public static readonly ValueConverter<UnitKind, string> UnitKind =
        new(v => ToDb(v), v => FromUnitKind(v));

    private static string ToDb(PropertyType v) => v switch
    {
        Domain.Properties.PropertyType.Hotel => "HOTEL",
        Domain.Properties.PropertyType.Villa => "VILLA",
        Domain.Properties.PropertyType.Apartment => "APARTMENT",
        Domain.Properties.PropertyType.Homestay => "HOMESTAY",
        Domain.Properties.PropertyType.Resort => "RESORT",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown property type.")
    };

    private static PropertyType FromPropertyType(string v) => v switch
    {
        "HOTEL" => Domain.Properties.PropertyType.Hotel,
        "VILLA" => Domain.Properties.PropertyType.Villa,
        "APARTMENT" => Domain.Properties.PropertyType.Apartment,
        "HOMESTAY" => Domain.Properties.PropertyType.Homestay,
        "RESORT" => Domain.Properties.PropertyType.Resort,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown property type.")
    };

    private static string ToDb(PropertyStatus v) => v switch
    {
        Domain.Properties.PropertyStatus.Draft => "DRAFT",
        Domain.Properties.PropertyStatus.InReview => "IN_REVIEW",
        Domain.Properties.PropertyStatus.Live => "LIVE",
        Domain.Properties.PropertyStatus.Suspended => "SUSPENDED",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown property status.")
    };

    private static PropertyStatus FromPropertyStatus(string v) => v switch
    {
        "DRAFT" => Domain.Properties.PropertyStatus.Draft,
        "IN_REVIEW" => Domain.Properties.PropertyStatus.InReview,
        "LIVE" => Domain.Properties.PropertyStatus.Live,
        "SUSPENDED" => Domain.Properties.PropertyStatus.Suspended,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown property status.")
    };

    private static string ToDb(HostStatus v) => v switch
    {
        Domain.Hosts.HostStatus.Pending => "PENDING",
        Domain.Hosts.HostStatus.Active => "ACTIVE",
        Domain.Hosts.HostStatus.Suspended => "SUSPENDED",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown host status.")
    };

    private static HostStatus FromHostStatus(string v) => v switch
    {
        "PENDING" => Domain.Hosts.HostStatus.Pending,
        "ACTIVE" => Domain.Hosts.HostStatus.Active,
        "SUSPENDED" => Domain.Hosts.HostStatus.Suspended,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown host status.")
    };

    private static string ToDb(UnitKind v) => v switch
    {
        Domain.Properties.UnitKind.Room => "ROOM",
        Domain.Properties.UnitKind.EntireUnit => "ENTIRE_UNIT",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown unit kind.")
    };

    private static UnitKind FromUnitKind(string v) => v switch
    {
        "ROOM" => Domain.Properties.UnitKind.Room,
        "ENTIRE_UNIT" => Domain.Properties.UnitKind.EntireUnit,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown unit kind.")
    };
}
