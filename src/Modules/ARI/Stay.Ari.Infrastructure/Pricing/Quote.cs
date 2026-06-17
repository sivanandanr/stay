namespace Stay.Ari.Infrastructure.Pricing;

/// <summary>The price for a single stay-night.</summary>
public sealed record NightPrice(DateOnly Date, decimal Price);

/// <summary>
/// A deterministic quote for a stay: the per-night breakdown and total, in one currency. This is the
/// frozen contract stored on the booking at hold time — never recomputed afterwards (BR-2).
/// </summary>
public sealed record Quote(string Currency, decimal Total, IReadOnlyList<NightPrice> Nights);
