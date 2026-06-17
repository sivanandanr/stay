using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stay.Booking.Infrastructure.Reminders;

/// <summary>Runs the pre-arrival reminder scheduler on a fixed interval.</summary>
public sealed class ReminderSchedulerService(ReminderScheduler scheduler, ILogger<ReminderSchedulerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var emitted = await scheduler.ReapDueAsync(DateTimeOffset.UtcNow, ct: stoppingToken);
                if (emitted > 0)
                    logger.LogInformation("Emitted {Count} pre-arrival reminder(s).", emitted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reminder scheduler pass failed; will retry.");
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
