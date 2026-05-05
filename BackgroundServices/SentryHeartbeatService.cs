using Microsoft.Extensions.Hosting;
using Sentry;

namespace VoiceTimerBot.BackgroundServices;

public class SentryHeartbeatService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SentrySdk.CaptureCheckIn(
                "bot-heartbeat",
                CheckInStatus.Ok,
                configureMonitorOptions: options =>
                {
                    options.Interval("* * * * *");
                    options.CheckInMargin = TimeSpan.FromMinutes(2);
                    options.MaxRuntime = TimeSpan.FromMinutes(1);
                });

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
