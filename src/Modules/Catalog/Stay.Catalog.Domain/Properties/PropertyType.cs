namespace Stay.Catalog.Domain.Properties;

/// <summary>Mirrors the <c>property_type</c> CHECK constraint on <c>catalog.property</c>.</summary>
public enum PropertyType
{
    Hotel,
    Villa,
    Apartment,
    Homestay,
    Resort
}

/// <summary>Mirrors the <c>status</c> CHECK constraint on <c>catalog.property</c>.</summary>
public enum PropertyStatus
{
    Draft,
    InReview,
    Live,
    Suspended
}
