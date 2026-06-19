using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stay.Search.Infrastructure;

/// <summary>
/// OpenSearch-backed property search read model over the REST API. Search-time pricing/availability
/// is a hot path (§5) — this is a thin, allocation-light client. Never the system of record: it's
/// rebuilt from catalog events and may lag.
/// </summary>
public sealed class OpenSearchPropertyIndex(HttpClient http) : IPropertySearchIndex
{
    private const string Index = "properties";

    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var mapping = new
        {
            mappings = new
            {
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "text" },
                    ["city"] = new { type = "keyword" },
                    ["country_code"] = new { type = "keyword" },
                    ["property_type"] = new { type = "keyword" },
                    ["status"] = new { type = "keyword" },
                    ["from_price"] = new { type = "double" },
                    ["location"] = new { type = "geo_point" },
                    ["thumbnail_url"] = new { type = "keyword", index = false },
                    ["rating"] = new { type = "double" },
                    ["review_count"] = new { type = "integer" },
                    ["currency"] = new { type = "keyword" },
                    ["amenities"] = new { type = "keyword" },
                    ["popularity"] = new { type = "integer" },
                    ["suggest"] = new { type = "completion" },
                },
            },
        };

        using var resp = await http.PutAsJsonAsync($"/{Index}", mapping, ct);
        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Contains("resource_already_exists_exception"))
                return; // already created — fine
        }
        resp.EnsureSuccessStatusCode();
    }

    public async Task IndexAsync(PropertySearchDocument doc, CancellationToken ct = default)
    {
        var body = new
        {
            id = doc.Id,
            name = doc.Name,
            city = doc.City,
            country_code = doc.CountryCode,
            property_type = doc.PropertyType,
            status = doc.Status,
            from_price = doc.FromPrice,
            location = new { lat = doc.Latitude, lon = doc.Longitude },
            thumbnail_url = doc.ThumbnailUrl,
            rating = doc.Rating,
            review_count = doc.ReviewCount,
            currency = doc.Currency,
            amenities = doc.Amenities ?? [],
            popularity = doc.Popularity,
            // Typeahead inputs: match on the property name or its city.
            suggest = new { input = new[] { doc.Name, doc.City }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() },
        };

        // refresh=wait_for makes the document searchable before the call returns.
        using var resp = await http.PutAsJsonAsync($"/{Index}/_doc/{doc.Id}?refresh=wait_for", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateStatusAsync(long propertyId, string status, CancellationToken ct = default)
    {
        var body = new { doc = new { status } };
        using var resp = await http.PostAsJsonAsync($"/{Index}/_update/{propertyId}?refresh=wait_for", body, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return; // the document isn't indexed yet — nothing to flip
        resp.EnsureSuccessStatusCode();
    }

    public async Task IncrementPopularityAsync(long propertyId, CancellationToken ct = default)
    {
        var body = new
        {
            script = new
            {
                lang = "painless",
                source = "ctx._source.popularity = (ctx._source.containsKey('popularity') ? ctx._source.popularity : 0) + 1",
            },
        };
        using var resp = await http.PostAsJsonAsync($"/{Index}/_update/{propertyId}?refresh=wait_for", body, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return; // not indexed yet (e.g. a not-yet-LIVE property) — nothing to bump
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateRatingAsync(long propertyId, double rating, int reviewCount, CancellationToken ct = default)
    {
        var body = new { doc = new { rating, review_count = reviewCount } };
        using var resp = await http.PostAsJsonAsync($"/{Index}/_update/{propertyId}?refresh=wait_for", body, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return; // not indexed yet (e.g. not-yet-LIVE) — nothing to refresh
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateFromPriceAsync(long propertyId, decimal fromPrice, string currency, CancellationToken ct = default)
    {
        var body = new
        {
            script = new
            {
                lang = "painless",
                source = "double p = params.p; if (ctx._source.from_price == null || p < ctx._source.from_price) { ctx._source.from_price = p; } ctx._source.currency = params.c;",
                @params = new { p = fromPrice, c = currency },
            },
        };
        using var resp = await http.PostAsJsonAsync($"/{Index}/_update/{propertyId}?refresh=wait_for", body, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return; // not indexed yet — nothing to refresh
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateAmenitiesAsync(long propertyId, IReadOnlyList<string> amenities, CancellationToken ct = default)
    {
        // Full replace — the event carries the complete desired set (event-carried state).
        var body = new { doc = new { amenities } };
        using var resp = await http.PostAsJsonAsync($"/{Index}/_update/{propertyId}?refresh=wait_for", body, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return; // not indexed yet (e.g. not-yet-LIVE) — nothing to refresh
        resp.EnsureSuccessStatusCode();
    }

    public Task<SearchResults> GetTrendingAsync(int limit, CancellationToken ct = default) =>
        SearchAsync(new PropertySearchQuery(null, null, null, Page: 0, PageSize: limit, Sort: SearchSort.Popularity), ct);

    public async Task<IReadOnlyList<TrendingDestination>> GetTrendingDestinationsAsync(int limit, CancellationToken ct = default)
    {
        // size:0 → aggregation-only; rank cities by summed popularity (booking momentum), LIVE only.
        var request = new
        {
            size = 0,
            query = new { @bool = new { filter = new object[] { new { term = new { status = "LIVE" } } } } },
            aggs = new
            {
                destinations = new
                {
                    terms = new { field = "city", size = limit, order = new { pop = "desc" } },
                    aggs = new
                    {
                        pop = new { sum = new { field = "popularity" } },
                        country = new { terms = new { field = "country_code", size = 1 } },
                    },
                },
            },
        };

        using var resp = await http.PostAsJsonAsync($"/{Index}/_search", request, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return [];
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var buckets = json.RootElement.GetProperty("aggregations").GetProperty("destinations").GetProperty("buckets");
        var results = new List<TrendingDestination>();
        foreach (var bucket in buckets.EnumerateArray())
        {
            var country = bucket.GetProperty("country").GetProperty("buckets");
            var countryCode = country.GetArrayLength() > 0 ? country[0].GetProperty("key").GetString()! : "";
            results.Add(new TrendingDestination(
                City: bucket.GetProperty("key").GetString()!,
                CountryCode: countryCode,
                PropertyCount: bucket.GetProperty("doc_count").GetInt64(),
                Popularity: (long)bucket.GetProperty("pop").GetProperty("value").GetDouble()));
        }
        return results;
    }

    public async Task<SearchResults> SearchAsync(PropertySearchQuery query, CancellationToken ct = default)
    {
        var filter = new List<object> { new { term = new { status = "LIVE" } } };
        if (!string.IsNullOrWhiteSpace(query.City))
            filter.Add(new { term = new { city = query.City } });
        if (!string.IsNullOrWhiteSpace(query.CountryCode))
            filter.Add(new { term = new { country_code = query.CountryCode } });
        if (!string.IsNullOrWhiteSpace(query.PropertyType))
            filter.Add(new { term = new { property_type = query.PropertyType } });

        if (query.MinPrice is not null || query.MaxPrice is not null)
        {
            var bounds = new Dictionary<string, object>();
            if (query.MinPrice is not null) bounds["gte"] = query.MinPrice;
            if (query.MaxPrice is not null) bounds["lte"] = query.MaxPrice;
            filter.Add(new { range = new { from_price = bounds } });
        }

        if (query.MinRating is not null)
            filter.Add(new { range = new { rating = new Dictionary<string, object> { ["gte"] = query.MinRating } } });

        // ALL listed amenities must be present (one term filter each → AND).
        if (query.Amenities is not null)
            foreach (var amenity in query.Amenities)
                filter.Add(new { term = new { amenities = amenity } });

        if (query.HasBoundingBox)
            filter.Add(new
            {
                geo_bounding_box = new
                {
                    location = new
                    {
                        top_left = new { lat = query.MaxLat, lon = query.MinLng },
                        bottom_right = new { lat = query.MinLat, lon = query.MaxLng },
                    },
                },
            });

        var boolQuery = new Dictionary<string, object> { ["filter"] = filter };
        if (!string.IsNullOrWhiteSpace(query.Text))
            boolQuery["must"] = new List<object> { new { match = new { name = query.Text } } };

        var request = new Dictionary<string, object>
        {
            ["from"] = query.Page * query.PageSize,
            ["size"] = query.PageSize,
            ["query"] = new { @bool = boolQuery },
        };
        var sort = SortClause(query.Sort);
        if (sort is not null) request["sort"] = sort;

        using var resp = await http.PostAsJsonAsync($"/{Index}/_search", request, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new SearchResults([], 0); // index not created yet → no results

        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseHits(json);
    }

    public async Task<SearchResults> GetSimilarAsync(long propertyId, int limit, CancellationToken ct = default)
    {
        // 1. Fetch the seed property's city/type from the index.
        using var getResp = await http.GetAsync($"/{Index}/_doc/{propertyId}", ct);
        if (!getResp.IsSuccessStatusCode)
            return new SearchResults([], 0);

        await using var seedStream = await getResp.Content.ReadAsStreamAsync(ct);
        using var seedJson = await JsonDocument.ParseAsync(seedStream, cancellationToken: ct);
        if (!seedJson.RootElement.TryGetProperty("_source", out var src))
            return new SearchResults([], 0);

        var city = src.TryGetProperty("city", out var c) ? c.GetString() : null;
        var type = src.TryGetProperty("property_type", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(city))
            return new SearchResults([], 0);

        // 2. Same-city LIVE properties, excluding the seed, same-type boosted, by popularity.
        var boolQuery = new Dictionary<string, object>
        {
            ["filter"] = new object[] { new { term = new { status = "LIVE" } }, new { term = new { city } } },
            ["must_not"] = new object[] { new { ids = new { values = new[] { propertyId.ToString() } } } },
        };
        if (!string.IsNullOrWhiteSpace(type))
            boolQuery["should"] = new object[] { new { term = new { property_type = type } } };

        var request = new Dictionary<string, object>
        {
            ["size"] = limit,
            ["query"] = new { @bool = boolQuery },
            ["sort"] = new object[] { new { popularity = new { order = "desc", missing = "_last" } } },
        };

        using var resp = await http.PostAsJsonAsync($"/{Index}/_search", request, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new SearchResults([], 0);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseHits(json);
    }

    public async Task<IReadOnlyList<PropertySuggestion>> SuggestAsync(string prefix, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return [];

        var request = new
        {
            _source = new[] { "id", "name", "city", "country_code", "status" },
            suggest = new
            {
                s = new
                {
                    prefix,
                    completion = new { field = "suggest", size = limit, skip_duplicates = true },
                },
            },
        };

        using var resp = await http.PostAsJsonAsync($"/{Index}/_search", request, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return [];
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var options = json.RootElement.GetProperty("suggest").GetProperty("s")[0].GetProperty("options");
        var results = new List<PropertySuggestion>();
        foreach (var opt in options.EnumerateArray())
        {
            var src = opt.GetProperty("_source");
            if (src.TryGetProperty("status", out var st) && st.GetString() != "LIVE")
                continue; // only suggest searchable properties
            results.Add(new PropertySuggestion(
                src.GetProperty("id").GetInt64(),
                src.GetProperty("name").GetString()!,
                src.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "",
                src.TryGetProperty("country_code", out var cc) ? cc.GetString() ?? "" : ""));
        }
        return results;
    }

    private static SearchResults ParseHits(JsonDocument json)
    {
        var hits = json.RootElement.GetProperty("hits");
        var total = hits.GetProperty("total").GetProperty("value").GetInt64();
        var items = new List<PropertySearchDocument>();
        foreach (var hit in hits.GetProperty("hits").EnumerateArray())
            items.Add(ToDocument(hit.GetProperty("_source")));
        return new SearchResults(items, total);
    }

    private static object? SortClause(SearchSort sort) => sort switch
    {
        SearchSort.PriceAsc => new object[] { new { from_price = new { order = "asc", missing = "_last" } } },
        SearchSort.PriceDesc => new object[] { new { from_price = new { order = "desc", missing = "_last" } } },
        SearchSort.Rating => new object[] { new { rating = new { order = "desc", missing = "_last" } } },
        SearchSort.Popularity => new object[] { new { popularity = new { order = "desc", missing = "_last" } } },
        _ => null, // Relevance → default _score order
    };

    private static PropertySearchDocument ToDocument(JsonElement src)
    {
        var location = src.GetProperty("location");

        var amenities = new List<string>();
        if (src.TryGetProperty("amenities", out var am) && am.ValueKind == JsonValueKind.Array)
            foreach (var a in am.EnumerateArray())
                if (a.GetString() is { } value) amenities.Add(value);

        return new PropertySearchDocument(
            src.GetProperty("id").GetInt64(),
            src.GetProperty("name").GetString()!,
            src.GetProperty("city").GetString()!,
            src.GetProperty("country_code").GetString()!,
            src.GetProperty("property_type").GetString()!,
            location.GetProperty("lat").GetDouble(),
            location.GetProperty("lon").GetDouble(),
            src.TryGetProperty("from_price", out var fp) && fp.ValueKind is not JsonValueKind.Null ? fp.GetDecimal() : null,
            src.GetProperty("status").GetString()!,
            ThumbnailUrl: src.TryGetProperty("thumbnail_url", out var th) && th.ValueKind is not JsonValueKind.Null ? th.GetString() : null,
            Rating: src.TryGetProperty("rating", out var rt) && rt.ValueKind is not JsonValueKind.Null ? rt.GetDouble() : null,
            ReviewCount: src.TryGetProperty("review_count", out var rc) && rc.ValueKind is not JsonValueKind.Null ? rc.GetInt32() : null,
            Currency: src.TryGetProperty("currency", out var cu) && cu.ValueKind is not JsonValueKind.Null ? cu.GetString()! : "INR",
            Amenities: amenities,
            Popularity: src.TryGetProperty("popularity", out var pop) && pop.ValueKind is not JsonValueKind.Null ? pop.GetInt32() : 0);
    }
}
