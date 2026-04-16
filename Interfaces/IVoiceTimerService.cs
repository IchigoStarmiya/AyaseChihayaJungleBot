namespace VoiceTimerBot.Interfaces;

public interface IVoiceTimerService
{
    bool IsRunning(ulong guildId);
    bool IsConfigured(ulong guildId);
    Task StartAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task StopAsync(ulong guildId, CancellationToken cancellationToken = default);
}
