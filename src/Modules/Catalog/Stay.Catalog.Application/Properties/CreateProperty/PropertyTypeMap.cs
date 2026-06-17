using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.CreateProperty;

/// <summary>Parses the wire string for <c>property_type</c> into the domain enum.</summary>
internal static class PropertyTypeMap
{
    public static bool TryParse(string? value, out PropertyType type)
    {
        switch (value)
        {
            case "HOTEL": type = PropertyType.Hotel; return true;
            case "VILLA": type = PropertyType.Villa; return true;
            case "APARTMENT": type = PropertyType.Apartment; return true;
            case "HOMESTAY": type = PropertyType.Homestay; return true;
            case "RESORT": type = PropertyType.Resort; return true;
            default: type = default; return false;
        }
    }
}
