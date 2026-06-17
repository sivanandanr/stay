using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stay.Payment.Infrastructure.Reconciliation;

/// <summary>Runs the payment poll-reconciler on a fixed interval (the daily/periodic §9 backstop).</summary>
public sealed class PaymentReconcilerService(PaymentReconciler reconciler, ILogger<PaymentReconcilerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reconciled = await reconciler.ReconcileAsync(StaleAfter, DateTimeOffset.UtcNow, ct: stoppingToken);
                if (reconciled > 0)
                    logger.LogInformation("Reconciled {Count} stale payment(s) against the gateway.", reconciled);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment reconciliation pass failed; will retry.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
