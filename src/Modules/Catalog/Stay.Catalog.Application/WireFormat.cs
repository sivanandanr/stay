using Stay.Catalog.Domain.Hosts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application;

/// <summary>Maps catalog domain enums to their wire/DB string values (same tokens the CHECK constraints use).</summary>
internal static class WireFormat
{
    public static string ToWire(PropertyType type) => type switch
    {
        PropertyType.Hotel => "HOTEL",
        PropertyType.Villa => "VILLA",
        PropertyType.Apartment => "APARTMENT",
        PropertyType.Homestay => "HOMESTAY",
        PropertyType.Resort => "RESORT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown property type.")
    };

    public static string ToWire(PropertyStatus status) => status switch
    {
        PropertyStatus.Draft => "DRAFT",
        PropertyStatus.InReview => "IN_REVIEW",
        PropertyStatus.Live => "LIVE",
        PropertyStatus.Suspended => "SUSPENDED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown property status.")
    };

    public static string ToWire(HostStatus status) => status switch
    {
        HostStatus.Pending => "PENDING",
        HostStatus.Active => "ACTIVE",
        HostStatus.Suspended => "SUSPENDED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown host status.")
    };
}
