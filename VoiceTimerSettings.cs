namespace VoiceTimerBot.Services;

public class VoiceTimerSettings
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public ulong OwnerId { get; set; }
    public List<VoiceTimerGuildSettings> Servers { get; set; } = [];
}

public class VoiceTimerGuildSettings
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong AuthorizedRoleId { get; set; }
    public string StartClipPath { get; set; } = "Audio/jungle.ogg";
    // Empty disables the 60-second spawn warning; set a clip path to opt this guild in.
    public string Warn60s { get; set; } = "";
    public string Warn40s { get; set; } = "Audio/jungle.ogg";
    public string Warn20s { get; set; } = "Audio/jungle.ogg";
    public string MaiJungle { get; set; } = "Audio/jungle.ogg";
    public string ZealClipPath { get; set; } = "";
    // The two boss cues are opt-in per guild: leave a path empty and that boss timer never runs
    // for this guild. The first boss spawns 4 minutes after the bot is activated, the second at 14.
    public string FirstBossClipPath { get; set; } = "";
    public string SecondBossClipPath { get; set; } = "";
}
