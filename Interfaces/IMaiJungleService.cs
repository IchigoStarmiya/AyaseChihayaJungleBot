namespace VoiceTimerBot.Interfaces;

public interface IMaiJungleService
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
