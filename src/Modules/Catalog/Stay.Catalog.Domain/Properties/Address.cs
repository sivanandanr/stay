namespace Stay.Catalog.Domain.Properties;

/// <summary>
/// Postal address stored as <c>jsonb</c> on <c>catalog.property.address</c>. A value object —
/// it has no identity of its own and is owned by the <see cref="Property"/>.
/// </summary>
public sealed record Address(
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string PostalCode,
    string CountryCode);
