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
    string Status,
    // Enrichment fields the guest cards/filters use. Optional + trailing so existing call sites and
    // the event projection keep compiling; they are populated as the enriching events are wired.
    string? ThumbnailUrl = null,
    double? Rating = null,
    int? ReviewCount = null,
    string Currency = "INR",
    IReadOnlyList<string>? Amenities = null,
    int Popularity = 0);

/// <summary>How results are ordered. Relevance is the default (text score, else recency-neutral).</summary>
public enum SearchSort
{
    Relevance,
    PriceAsc,
    PriceDesc,
    Rating,
    Popularity,
}

/// <summary>
/// Guest search criteria. Page is 0-based; only LIVE properties are ever returned. Filters are all
/// optional and AND-combined; <see cref="Amenities"/> requires ALL listed amenities. A geo bounding
/// box (<see cref="MinLat"/>…<see cref="MaxLng"/>) supports "search this area" on the map.
/// </summary>
public sealed record PropertySearchQuery(
    string? City,
    string? CountryCode,
    string? Text,
    int Page,
    int PageSize,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    double? MinRating = null,
    string? PropertyType = null,
    IReadOnlyList<string>? Amenities = null,
    SearchSort Sort = SearchSort.Relevance,
    double? MinLat = null,
    double? MaxLat = null,
    double? MinLng = null,
    double? MaxLng = null)
{
    /// <summary>True when a full geo bounding box was supplied.</summary>
    public bool HasBoundingBox => MinLat is not null && MaxLat is not null && MinLng is not null && MaxLng is not null;
}

/// <summary>A page of search results plus the total match count.</summary>
public sealed record SearchResults(IReadOnlyList<PropertySearchDocument> Items, long Total);

/// <summary>A trending destination (city) — how many LIVE properties it has and its booking momentum.</summary>
public sealed record TrendingDestination(string City, string CountryCode, long PropertyCount, long Popularity);

/// <summary>A typeahead suggestion (a property name match), with enough to navigate or search.</summary>
public sealed record PropertySuggestion(long Id, string Name, string City, string CountryCode);

/// <summary>The property search read model (OpenSearch). Eventually consistent + cached (CLAUDE.md §4).</summary>
public interface IPropertySearchIndex
{
    Task EnsureIndexAsync(CancellationToken ct = default);
    Task IndexAsync(PropertySearchDocument document, CancellationToken ct = default);

    /// <summary>Updates just the visibility status of an already-indexed property (publish/reject).</summary>
    Task UpdateStatusAsync(long propertyId, string status, CancellationToken ct = default);

    /// <summary>Bumps a property's popularity score (the trending signal) on a confirmed booking.</summary>
    Task IncrementPopularityAsync(long propertyId, CancellationToken ct = default);

    /// <summary>Refreshes a property's rating + review count in the index (from published reviews).</summary>
    Task UpdateRatingAsync(long propertyId, double rating, int reviewCount, CancellationToken ct = default);

    /// <summary>Lowers a property's "from" price in the index to the minimum lead-in rate seen.</summary>
    Task UpdateFromPriceAsync(long propertyId, decimal fromPrice, string currency, CancellationToken ct = default);

    /// <summary>Overwrites a property's amenity set in the index (full replace, from the catalog).</summary>
    Task UpdateAmenitiesAsync(long propertyId, IReadOnlyList<string> amenities, CancellationToken ct = default);

    Task<SearchResults> SearchAsync(PropertySearchQuery query, CancellationToken ct = default);

    /// <summary>The most popular LIVE properties (trending), highest score first.</summary>
    Task<SearchResults> GetTrendingAsync(int limit, CancellationToken ct = default);

    /// <summary>Trending destinations (cities), by booking momentum — for the home discovery tiles.</summary>
    Task<IReadOnlyList<TrendingDestination>> GetTrendingDestinationsAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Properties similar to a seed (same city, same type boosted, by popularity), excluding the seed —
    /// powers "similar stays" and the personalized "recommended for you" surfaces. Empty if the seed
    /// isn't indexed.
    /// </summary>
    Task<SearchResults> GetSimilarAsync(long propertyId, int limit, CancellationToken ct = default);

    /// <summary>Fast prefix typeahead over property names/cities (completion suggester), LIVE only.</summary>
    Task<IReadOnlyList<PropertySuggestion>> SuggestAsync(string prefix, int limit, CancellationToken ct = default);
}
