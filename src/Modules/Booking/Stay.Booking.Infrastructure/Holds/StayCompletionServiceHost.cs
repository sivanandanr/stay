using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>Runs stay completion on a fixed interval (hourly is ample for checkout-day transitions).</summary>
public sealed class StayCompletionServiceHost(StayCompletionService completion, ILogger<StayCompletionServiceHost> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await completion.ReapAsync(DateTimeOffset.UtcNow, ct: stoppingToken);
                if (completed > 0)
                    logger.LogInformation("Completed {Count} stay(s).", completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Stay completion pass failed; will retry."); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
