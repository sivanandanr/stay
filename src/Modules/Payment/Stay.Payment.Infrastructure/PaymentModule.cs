using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Payment.Contracts;
using Stay.Payment.Infrastructure.Disputes;
using Stay.Payment.Infrastructure.Payouts;
using Stay.Payment.Infrastructure.Reconciliation;
using Stay.Payment.Infrastructure.Webhooks;

namespace Stay.Payment.Infrastructure;

/// <summary>
/// Payment context: registers the <see cref="IPaymentGateway"/> port (defaults to the local fake;
/// a real adapter over the shared PaymentGateway service replaces it in higher environments), the
/// webhook ingestion service (source of truth for async PSP state, §9), and owner payouts via
/// Razorpay Route (<see cref="IPayoutGateway"/>).
/// </summary>
public sealed class PaymentModule : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton<IPaymentGateway, FakePaymentGateway>();
        services.TryAddSingleton<IPayoutGateway, FakePayoutGateway>();

        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");
        services.AddSingleton(new PaymentWebhookService(connectionString));
        services.AddSingleton(sp => new PaymentReconciler(connectionString, sp.GetRequiredService<IPaymentGateway>()));
        services.AddSingleton(sp => new LedgerReconciler(connectionString, sp.GetRequiredService<IPaymentGateway>()));
        services.AddSingleton(sp => new PayoutService(connectionString, sp.GetRequiredService<IPayoutGateway>()));
        services.AddSingleton(new DisputeService(connectionString));
        services.AddHostedService<PaymentReconcilerService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Finance/ops generates a host's payout statement for a period (DRAFT).
        endpoints.MapPost("/api/v1/admin/payouts", async (
            GeneratePayoutRequest request, PayoutService payouts, CancellationToken ct) =>
            (await payouts.GenerateAsync(request, ct)).ToHttp(p => Results.Created($"/api/v1/admin/payouts/{p.Id}", p)))
        .RequireAuthorization("ops")
        .WithName("GeneratePayout");

        // Finance/ops executes a payout against Razorpay Route (audited §10).
        endpoints.MapPost("/api/v1/admin/payouts/{payoutId:long}/execute", async (
            long payoutId, ExecutePayoutBody body, ClaimsPrincipal user, PayoutService payouts, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await payouts.ExecuteAsync(payoutId, body.LinkedAccountRef, sub, ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("ExecutePayout");

        // Finance/ops runs the daily ledger reconciliation; a non-empty report is the finance alert (Gate G2).
        endpoints.MapPost("/api/v1/admin/payments/reconcile", async (
            DateTimeOffset? cutoff, LedgerReconciler reconciler, CancellationToken ct) =>
            Results.Ok(await reconciler.ReconcileAsync(cutoff ?? DateTimeOffset.UtcNow, ct: ct)))
        .RequireAuthorization("ops")
        .WithName("ReconcileLedger");

        // Finance opens a dispute (typically webhook-fed; idempotent by PSP dispute id).
        endpoints.MapPost("/api/v1/admin/disputes", async (
            OpenDisputeRequest request, DisputeService disputes, CancellationToken ct) =>
            (await disputes.OpenAsync(request, ct)).ToHttp(d => Results.Created($"/api/v1/admin/disputes/{d.Id}", d)))
        .RequireAuthorization("ops")
        .WithName("OpenDispute");

        // Finance resolves a dispute (WON/LOST/ACCEPTED, mandatory note, audited §10).
        endpoints.MapPost("/api/v1/admin/disputes/{disputeId:long}/resolve", async (
            long disputeId, ResolveDisputeRequest request, ClaimsPrincipal user, DisputeService disputes, CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            return (await disputes.ResolveAsync(disputeId, sub, request.Outcome, request.Resolution, ct)).ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("ResolveDispute");
    }

    private sealed record ExecutePayoutBody(string LinkedAccountRef);
}
