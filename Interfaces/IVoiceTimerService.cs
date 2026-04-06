namespace VoiceTimerBot.Interfaces;

public interface IVoiceTimerService
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
