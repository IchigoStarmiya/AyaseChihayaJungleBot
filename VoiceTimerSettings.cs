namespace VoiceTimerBot.Services;

public class VoiceTimerSettings
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public string StartClipPath { get; set; } = "Audio/jungle.ogg";
    public string Warn40s { get; set; } = "Audio/jungle.ogg";
    public string Warn20s { get; set; } = "Audio/jungle.ogg";
    public string MaiJungle { get; set; } = "Audio/jungle.ogg";
    public string FfmpegPath { get; set; } = "ffmpeg";
}
