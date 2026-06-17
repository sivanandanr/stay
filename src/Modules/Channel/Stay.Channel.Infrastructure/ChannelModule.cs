using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Channel.Contracts;

namespace Stay.Channel.Infrastructure;

/// <summary>
/// Channel manager / PMS context: owners connect a property + map rooms; the channel pushes ordered,
/// idempotent ARI updates we apply to the calendars (Gate G5). Reverse sync + reconciliation build on
/// the <c>ChannelAriApplied</c> event and the sync log.
/// </summary>
public sealed class ChannelModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");

        services.AddSingleton(new ChannelConnectionService(connectionString));
        services.AddSingleton(new ChannelIngestService(connectionString));
        // No real channel SDK in Stay (§9-style port); a per-provider adapter replaces this in prod.
        services.AddSingleton<IChannelClient, FakeChannelClient>();
        services.AddSingleton(sp => new ChannelReconciler(connectionString, sp.GetRequiredService<IChannelClient>()));
        services.AddSingleton(new ConflictResolutionService(connectionString));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Owner connects a property to a channel manager / PMS.
        endpoints.MapPost("/api/v1/channels", async (
            ConnectChannelRequest request, ChannelConnectionService channels, CancellationToken ct) =>
            (await channels.ConnectAsync(request, ct))
                .ToHttp(c => Results.Created($"/api/v1/channels/{c.Id}", c)))
        .RequireAuthorization()
        .WithName("ConnectChannel");

        // Owner maps an external room/rate code to one of the property's room types.
        endpoints.MapPost("/api/v1/channels/{channelConnectionId:long}/rooms", async (
            long channelConnectionId, MapRoomRequest request, ChannelConnectionService channels, CancellationToken ct) =>
            (await channels.MapRoomAsync(channelConnectionId, request, ct))
                .ToHttp(id => Results.Created($"/api/v1/channels/{channelConnectionId}/rooms/{id}", new { id })))
        .RequireAuthorization()
        .WithName("MapChannelRoom");

        // The channel manager pushes an ordered ARI message. Machine-to-machine; partner
        // client-credentials is the real principal (Phase 8/9) — "ops" is the interim gate.
        endpoints.MapPost("/api/v1/channels/{channelConnectionId:long}/ari", async (
            long channelConnectionId, AriIngestMessage message, ChannelIngestService ingest, CancellationToken ct) =>
            (await ingest.IngestAsync(channelConnectionId, message, ct)).ToHttp(Results.Ok))
        .RequireAuthorization("ops")
        .WithName("IngestChannelAri");

        // Ops/scheduler runs reconciliation over a date window; opens sync_conflict rows for drift.
        endpoints.MapPost("/api/v1/channels/{channelConnectionId:long}/reconcile", async (
            long channelConnectionId, DateOnly from, DateOnly to, ChannelReconciler reconciler, CancellationToken ct) =>
            (await reconciler.ReconcileAsync(channelConnectionId, from, to, ct)).ToHttp(Results.Ok))
        .RequireAuthorization("ops")
        .WithName("ReconcileChannel");

        // Ops resolves or escalates a sync conflict (mandatory note, audited §10).
        endpoints.MapPost("/api/v1/channels/conflicts/{conflictId:long}/resolve", async (
            long conflictId, ResolveConflictRequest request, ClaimsPrincipal user,
            ConflictResolutionService conflicts, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await conflicts.ResolveAsync(conflictId, sub, request.Resolution, request.Escalate, ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("ResolveChannelConflict");
    }
}
