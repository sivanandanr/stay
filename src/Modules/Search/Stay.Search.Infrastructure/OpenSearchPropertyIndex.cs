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
                    ["location"] = new { type = "geo_point" }
                }
            }
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
            location = new { lat = doc.Latitude, lon = doc.Longitude }
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

    public async Task<SearchResults> SearchAsync(PropertySearchQuery query, CancellationToken ct = default)
    {
        var filter = new List<object> { new { term = new { status = "LIVE" } } };
        if (!string.IsNullOrWhiteSpace(query.City))
            filter.Add(new { term = new { city = query.City } });
        if (!string.IsNullOrWhiteSpace(query.CountryCode))
            filter.Add(new { term = new { country_code = query.CountryCode } });

        var must = new List<object>();
        if (!string.IsNullOrWhiteSpace(query.Text))
            must.Add(new { match = new { name = query.Text } });

        var request = new
        {
            from = query.Page * query.PageSize,
            size = query.PageSize,
            query = new { @bool = new { filter, must } }
        };

        using var resp = await http.PostAsJsonAsync($"/{Index}/_search", request, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new SearchResults([], 0); // index not created yet → no results

        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var hits = json.RootElement.GetProperty("hits");
        var total = hits.GetProperty("total").GetProperty("value").GetInt64();

        var items = new List<PropertySearchDocument>();
        foreach (var hit in hits.GetProperty("hits").EnumerateArray())
            items.Add(ToDocument(hit.GetProperty("_source")));

        return new SearchResults(items, total);
    }

    private static PropertySearchDocument ToDocument(JsonElement src)
    {
        var location = src.GetProperty("location");
        return new PropertySearchDocument(
            src.GetProperty("id").GetInt64(),
            src.GetProperty("name").GetString()!,
            src.GetProperty("city").GetString()!,
            src.GetProperty("country_code").GetString()!,
            src.GetProperty("property_type").GetString()!,
            location.GetProperty("lat").GetDouble(),
            location.GetProperty("lon").GetDouble(),
            src.TryGetProperty("from_price", out var fp) && fp.ValueKind is not JsonValueKind.Null ? fp.GetDecimal() : null,
            src.GetProperty("status").GetString()!);
    }
}
