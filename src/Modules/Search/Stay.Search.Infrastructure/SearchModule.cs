using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stay.BuildingBlocks;

namespace Stay.Search.Infrastructure;

/// <summary>
/// Search context: the guest-facing property discovery surface. Browse/search is anonymous (§6).
/// </summary>
public sealed class SearchModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var url = config["Search:OpenSearchUrl"] ?? "http://localhost:9200";
        services.AddSingleton<IPropertySearchIndex>(_ =>
            new OpenSearchPropertyIndex(new HttpClient { BaseAddress = new Uri(url) }));
        services.AddSingleton<SearchIndexProjection>();
        services.AddSingleton<PopularityProjection>();
        services.AddSingleton<RatingProjection>();
        services.AddSingleton<SearchPriceProjection>();
        services.AddSingleton<SearchAmenitiesProjection>();
        services.AddHostedService<SearchIndexInitializer>();
        services.AddHostedService<SearchIndexerConsumer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        MapSearch(endpoints);
        MapSuggest(endpoints);
        MapTrending(endpoints);
        MapDestinations(endpoints);
        MapSimilar(endpoints);
    }

    // Fast prefix typeahead for the search box (completion suggester).
    private static void MapSuggest(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/search/suggest", async (string q, int? limit, IPropertySearchIndex index, CancellationToken ct) =>
            Results.Ok(new { items = await index.SuggestAsync(q, Math.Clamp(limit ?? 6, 1, 15), ct) }))
            .WithName("SearchSuggest"); // anonymous

    // "Similar stays" + the seed for "recommended for you" (mobile seeds it from the guest's last trip).
    private static void MapSimilar(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/discovery/similar/{propertyId:long}", async (
            long propertyId, int? limit, IPropertySearchIndex index, CancellationToken ct) =>
        {
            var results = await index.GetSimilarAsync(propertyId, Math.Clamp(limit ?? 10, 1, 30), ct);
            return Results.Ok(new { total = results.Total, items = results.Items });
        })
        .WithName("SimilarStays"); // anonymous discovery surface

    private static void MapDestinations(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/discovery/destinations", async (int? limit, IPropertySearchIndex index, CancellationToken ct) =>
            Results.Ok(new { items = await index.GetTrendingDestinationsAsync(Math.Clamp(limit ?? 8, 1, 30), ct) }))
            .WithName("TrendingDestinations"); // anonymous discovery surface

    private static void MapSearch(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/search/properties", async (
            string? city, string? country, string? q, int? page, int? pageSize,
            decimal? minPrice, decimal? maxPrice, double? minRating, string? type, string? amenities, string? sort,
            double? minLat, double? maxLat, double? minLng, double? maxLng,
            IPropertySearchIndex index, CancellationToken ct) =>
        {
            var query = new PropertySearchQuery(
                City: city,
                CountryCode: country,
                Text: q,
                Page: Math.Max(0, (page ?? 1) - 1),      // API is 1-based, OpenSearch is 0-based
                PageSize: Math.Clamp(pageSize ?? 20, 1, 100),
                MinPrice: minPrice,
                MaxPrice: maxPrice,
                MinRating: minRating,
                PropertyType: type,
                Amenities: amenities?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Sort: ParseSort(sort),
                MinLat: minLat,
                MaxLat: maxLat,
                MinLng: minLng,
                MaxLng: maxLng);

            var results = await index.SearchAsync(query, ct);
            return Results.Ok(new { total = results.Total, items = results.Items });
        })
        .WithName("SearchProperties"); // anonymous — no RequireAuthorization

    private static void MapTrending(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/discovery/trending", async (int? limit, IPropertySearchIndex index, CancellationToken ct) =>
        {
            var results = await index.GetTrendingAsync(Math.Clamp(limit ?? 10, 1, 50), ct);
            return Results.Ok(new { total = results.Total, items = results.Items });
        })
        .WithName("Trending"); // anonymous discovery surface

    private static SearchSort ParseSort(string? sort) => sort?.ToLowerInvariant() switch
    {
        "price_asc" => SearchSort.PriceAsc,
        "price_desc" => SearchSort.PriceDesc,
        "rating" => SearchSort.Rating,
        _ => SearchSort.Relevance,
    };
}

/// <summary>Creates the search index at startup (idempotent); a missing index is otherwise built on first write.</summary>
internal sealed class SearchIndexInitializer(IPropertySearchIndex index, ILogger<SearchIndexInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await index.EnsureIndexAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not ensure the search index at startup; it will be created on first write.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
