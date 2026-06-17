using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.AddRoomType;

/// <summary>Parses the wire string for <c>unit_kind</c> into the domain enum.</summary>
internal static class UnitKindMap
{
    public static bool TryParse(string? value, out UnitKind kind)
    {
        switch (value)
        {
            case "ROOM": kind = UnitKind.Room; return true;
            case "ENTIRE_UNIT": kind = UnitKind.EntireUnit; return true;
            default: kind = default; return false;
        }
    }
}
