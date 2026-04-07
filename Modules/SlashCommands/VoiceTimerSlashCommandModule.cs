using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using VoiceTimerBot.Interfaces;

namespace VoiceTimerBot.Modules.SlashCommands;

[UsedImplicitly]
public class VoiceTimerSlashCommandModule(IVoiceTimerService voiceTimerService, IMaiJungleService maiJungleService, IConfiguration configuration, RestClient restClient)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("starttimer", "Start (or reset) the voice channel timer")]
    [UsedImplicitly]
    public async Task StartTimer()
    {
        if (!await IsCouncilMemberAsync())
        {
            await RespondEphemeralAsync("You do not have permission to use this command.");
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        try
        {
            await voiceTimerService.StartAsync();
            await Context.Interaction.ModifyResponseAsync(m => m.WithContent("Timer started."));
        }
        catch (Exception ex)
        {
            await Context.Interaction.ModifyResponseAsync(m =>
                m.WithContent($"Failed to start the timer: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    [SlashCommand("stoptimer", "Stop the voice channel timer and disconnect the bot")]
    [UsedImplicitly]
    public async Task StopTimer()
    {
        if (!await IsCouncilMemberAsync())
        {
            await RespondEphemeralAsync("You do not have permission to use this command.");
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        if (!voiceTimerService.IsRunning)
        {
            await Context.Interaction.ModifyResponseAsync(m => m.WithContent("The timer is not running."));
            return;
        }

        try
        {
            await voiceTimerService.StopAsync();
            await Context.Interaction.ModifyResponseAsync(m => m.WithContent("Timer stopped and bot disconnected."));
        }
        catch (Exception ex)
        {
            await Context.Interaction.ModifyResponseAsync(m =>
                m.WithContent($"Failed to stop the timer: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private async Task<bool> IsCouncilMemberAsync()
    {
        var guildId = Context.Interaction.GuildId;
        if (guildId is null) return false;
        var councilRoles = configuration.GetSection("CouncilRole").Get<ulong[]>() ?? [];
        var member = await restClient.GetGuildUserAsync(guildId.Value, Context.User.Id);
        return member.RoleIds.Any(r => councilRoles.Contains(r));
    }

    private Task RespondEphemeralAsync(string content) =>
        Context.Interaction.SendResponseAsync(
            InteractionCallback.Message(new InteractionMessageProperties
            {
                Content = content,
                Flags = MessageFlags.Ephemeral
            }));
}
