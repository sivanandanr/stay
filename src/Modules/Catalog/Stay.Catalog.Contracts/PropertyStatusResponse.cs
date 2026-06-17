namespace Stay.Catalog.Contracts;

/// <summary>The outcome of a moderation transition — the property and its new status.</summary>
public sealed record PropertyStatusResponse(long PropertyId, string Status);
