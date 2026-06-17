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
        services.AddHostedService<SearchIndexInitializer>();
        services.AddHostedService<SearchIndexerConsumer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/search/properties", async (
            string? city, string? country, string? q, int? page, int? pageSize,
            IPropertySearchIndex index, CancellationToken ct) =>
        {
            var query = new PropertySearchQuery(
                City: city,
                CountryCode: country,
                Text: q,
                Page: Math.Max(0, (page ?? 1) - 1),      // API is 1-based, OpenSearch is 0-based
                PageSize: Math.Clamp(pageSize ?? 20, 1, 100));

            var results = await index.SearchAsync(query, ct);
            return Results.Ok(new { total = results.Total, items = results.Items });
        })
        .WithName("SearchProperties"); // anonymous — no RequireAuthorization
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
