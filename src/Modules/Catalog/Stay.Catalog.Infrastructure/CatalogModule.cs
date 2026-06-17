using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;

namespace Stay.Catalog.Infrastructure;

public sealed class CatalogModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/catalog/ping", () => Results.Ok(new { module = "Catalog", ok = true }));
}
