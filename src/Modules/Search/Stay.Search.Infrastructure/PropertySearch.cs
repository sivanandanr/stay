namespace Stay.Search.Infrastructure;

/// <summary>A denormalized, search-optimized view of a LIVE property (the OpenSearch read model).</summary>
public sealed record PropertySearchDocument(
    long Id,
    string Name,
    string City,
    string CountryCode,
    string PropertyType,
    double Latitude,
    double Longitude,
    decimal? FromPrice,
    string Status);

/// <summary>Guest search criteria. Page is 0-based; only LIVE properties are ever returned.</summary>
public sealed record PropertySearchQuery(string? City, string? CountryCode, string? Text, int Page, int PageSize);

/// <summary>A page of search results plus the total match count.</summary>
public sealed record SearchResults(IReadOnlyList<PropertySearchDocument> Items, long Total);

/// <summary>The property search read model (OpenSearch). Eventually consistent + cached (CLAUDE.md §4).</summary>
public interface IPropertySearchIndex
{
    Task EnsureIndexAsync(CancellationToken ct = default);
    Task IndexAsync(PropertySearchDocument document, CancellationToken ct = default);

    /// <summary>Updates just the visibility status of an already-indexed property (publish/reject).</summary>
    Task UpdateStatusAsync(long propertyId, string status, CancellationToken ct = default);

    Task<SearchResults> SearchAsync(PropertySearchQuery query, CancellationToken ct = default);
}
