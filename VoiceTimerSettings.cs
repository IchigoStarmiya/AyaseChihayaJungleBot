namespace VoiceTimerBot.Services;

public class VoiceTimerSettings
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public List<VoiceTimerGuildSettings> Servers { get; set; } = [];
}

public class VoiceTimerGuildSettings
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong AuthorizedRoleId { get; set; }
    public string StartClipPath { get; set; } = "Audio/jungle.ogg";
    public string Warn60s { get; set; } = "Audio/jungle.ogg";
    public string Warn40s { get; set; } = "Audio/jungle.ogg";
    public string Warn20s { get; set; } = "Audio/jungle.ogg";
    public string MaiJungle { get; set; } = "Audio/jungle.ogg";
}
