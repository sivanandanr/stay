using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.BuildingBlocks.Http;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Application.Hosts.ApproveHost;
using Stay.Catalog.Application.Hosts.GetMyHost;
using Stay.Catalog.Application.Hosts.RegisterHost;
using Stay.Catalog.Application.Hosts.RejectHost;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Application.Properties.AddRoomType;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Application.Properties.GetPropertyById;
using Stay.Catalog.Application.Properties.PublishProperty;
using Stay.Catalog.Application.Properties.RejectProperty;
using Stay.Catalog.Application.Properties.SubmitForReview;
using Stay.Catalog.Contracts;
using Stay.Catalog.Infrastructure.Persistence;

namespace Stay.Catalog.Infrastructure;

public sealed class CatalogModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()));
        services.AddScoped<ICatalogDbContext>(sp => sp.GetRequiredService<CatalogDbContext>());

        services.AddCqrs(typeof(CreatePropertyHandler).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/catalog/ping", () => Results.Ok(new { module = "Catalog", ok = true }));

        MapRegisterHost(endpoints);
        MapGetMyHost(endpoints);
        MapApproveHost(endpoints);
        MapRejectHost(endpoints);
        MapCreateProperty(endpoints);
        MapGetProperty(endpoints);
        MapAddRoomType(endpoints);
        MapSubmitForReview(endpoints);
        MapPublishProperty(endpoints);
        MapRejectProperty(endpoints);
        MapTestEvent(endpoints);
    }

    // Owner submits their draft for moderation (DRAFT → IN_REVIEW).
    private static void MapSubmitForReview(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/properties/{propertyId:long}/submit", async (
            long propertyId,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthenticated();

            var result = await dispatcher.Send(new SubmitPropertyForReviewCommand(sub, propertyId), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("SubmitPropertyForReview");

    // Moderator decision: publish a property (IN_REVIEW → LIVE). ops/admin only.
    private static void MapPublishProperty(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/admin/properties/{propertyId:long}/publish", async (
            long propertyId,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var actor = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(actor))
                return Unauthenticated();

            var result = await dispatcher.Send(new PublishPropertyCommand(actor, propertyId), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("PublishProperty");

    // Moderator decision: reject a property back to DRAFT (mandatory reason). ops/admin only.
    private static void MapRejectProperty(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/admin/properties/{propertyId:long}/reject", async (
            long propertyId,
            RejectPropertyRequest request,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var actor = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(actor))
                return Unauthenticated();

            var result = await dispatcher.Send(new RejectPropertyCommand(actor, propertyId, request.Reason), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("RejectProperty");

    // The authenticated owner's identity. The subject claim is the only owner source — never the body.
    private static string? SubjectOf(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult Unauthenticated() => ResultHttpExtensions.Problem(
        new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

    // First-login owner provisioning. Idempotent: repeated calls return the same host.
    private static void MapRegisterHost(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/hosts/register", async (
            RegisterHostRequest request,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthenticated();

            var result = await dispatcher.Send(new RegisterHostCommand(sub, request.DisplayName), ct);
            return result.ToHttp(id => Results.Created($"/api/v1/hosts/{id}", new { hostId = id }));
        })
        .RequireAuthorization()
        .WithName("RegisterHost");

    // The caller's own host profile (e.g. to poll approval status).
    private static void MapGetMyHost(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/hosts/me", async (
            ClaimsPrincipal user,
            IQueryDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthenticated();

            var result = await dispatcher.Send(new GetMyHostQuery(sub), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("GetMyHost");

    // Admin decision: approve a host so it may list. Authorized to the ops/admin role server-side.
    private static void MapApproveHost(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/admin/hosts/{hostId:long}/approve", async (
            long hostId,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var actor = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(actor))
                return Unauthenticated();

            var result = await dispatcher.Send(new ApproveHostCommand(actor, hostId), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("ApproveHost");

    // Admin decision: reject a host (mandatory reason). Authorized to the ops/admin role server-side.
    private static void MapRejectHost(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/admin/hosts/{hostId:long}/reject", async (
            long hostId,
            RejectHostRequest request,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var actor = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(actor))
                return Unauthenticated();

            var result = await dispatcher.Send(new RejectHostCommand(actor, hostId, request.Reason), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("RejectHost");

    // Owner-authorized listing creation. The token subject is the owner; the body never carries it.
    private static void MapCreateProperty(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/properties", async (
            CreatePropertyRequest request,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthenticated();

            var command = new CreatePropertyCommand(
                OwnerSub: sub,
                Name: request.Name,
                PropertyType: request.PropertyType,
                Description: request.Description,
                StarRating: request.StarRating,
                Latitude: request.Latitude,
                Longitude: request.Longitude,
                CountryCode: request.CountryCode,
                CityId: request.CityId,
                Address: request.Address,
                DefaultCurrency: request.DefaultCurrency,
                Timezone: request.Timezone,
                CheckInTime: request.CheckInTime,
                CheckOutTime: request.CheckOutTime);

            var result = await dispatcher.Send(command, ct);
            return result.ToHttp(id => Results.Created($"/api/v1/properties/{id}", new { id }));
        })
        .RequireAuthorization()
        .WithName("CreateProperty");

    // Owner-scoped read: a property is visible only to the host that owns it (BR-9).
    private static void MapGetProperty(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/properties/{id:long}", async (
            long id,
            ClaimsPrincipal user,
            IQueryDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthenticated();

            var result = await dispatcher.Send(new GetPropertyByIdQuery(sub, id), ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("GetProperty");

    // Owner-authorized: add a room type to a property the caller owns.
    private static void MapAddRoomType(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/properties/{propertyId:long}/room-types", async (
            long propertyId,
            AddRoomTypeRequest request,
            ClaimsPrincipal user,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = SubjectOf(user);
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthenticated();

            var command = new AddRoomTypeCommand(
                OwnerSub: sub,
                PropertyId: propertyId,
                Name: request.Name,
                UnitKind: request.UnitKind,
                TotalUnits: request.TotalUnits,
                BaseOccupancy: request.BaseOccupancy,
                MaxOccupancy: request.MaxOccupancy,
                MaxAdults: request.MaxAdults,
                MaxChildren: request.MaxChildren,
                BedConfig: request.BedConfig,
                SizeSqm: request.SizeSqm);

            var result = await dispatcher.Send(command, ct);
            return result.ToHttp(id => Results.Created($"/api/v1/properties/{propertyId}/room-types/{id}", new { id }));
        })
        .RequireAuthorization()
        .WithName("AddRoomType");

    // P0-A6 producer: makes a catalog write AND emits a TestEvent in ONE transaction, so the
    // event can never exist without the state change, and vice-versa (no dual-write, BR-11).
    private static void MapTestEvent(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/catalog/test-event", async (
            IOutboxWriter outbox,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var connectionString = config.GetConnectionString("Stay")
                ?? throw new InvalidOperationException("Missing connection string 'Stay'.");

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var code = $"TEST_{Guid.NewGuid():N}";
            var amenityId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO catalog.amenity (code, category, label)
                VALUES (@code, 'TEST', 'Outbox round-trip probe')
                RETURNING id
                """,
                new { code }, tx, cancellationToken: ct));

            var @event = new TestEvent(Guid.NewGuid(), amenityId, DateTimeOffset.UtcNow);
            await outbox.WriteAsync(conn, tx, "catalog", @event, ct);

            await tx.CommitAsync(ct);

            return Results.Accepted(
                $"/api/v1/catalog/amenities/{amenityId}",
                new { @event.EventId, amenityId });
        });
}
