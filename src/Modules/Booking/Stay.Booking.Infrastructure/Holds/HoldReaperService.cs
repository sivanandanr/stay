using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stay.Booking.Infrastructure.Holds;

/// <summary>Runs the <see cref="HoldReaper"/> on a fixed interval (BR-3).</summary>
public sealed class HoldReaperService(HoldReaper reaper, ILogger<HoldReaperService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reaped = await reaper.ReapAsync(ct: stoppingToken);
                if (reaped > 0)
                    logger.LogInformation("Hold reaper expired {Count} lapsed booking(s).", reaped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hold reaper pass failed; will retry.");
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
