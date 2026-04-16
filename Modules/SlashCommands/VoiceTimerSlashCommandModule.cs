using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using VoiceTimerBot.Interfaces;
using VoiceTimerBot.Services;

namespace VoiceTimerBot.Modules.SlashCommands;

[UsedImplicitly]
public class VoiceTimerSlashCommandModule(IVoiceTimerService voiceTimerService, IOptions<VoiceTimerSettings> options, RestClient restClient)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("starttimer", "Start (or reset) the voice channel timer")]
    [UsedImplicitly]
    public async Task StartTimer()
    {
        var guildId = Context.Interaction.GuildId;
        if (guildId is null)
        {
            await RespondEphemeralAsync("This command must be used in a server.");
            return;
        }

        if (!voiceTimerService.IsConfigured(guildId.Value))
        {
            await RespondEphemeralAsync("This server is not configured for the jungle timer.");
            return;
        }

        if (!await IsAuthorizedAsync(guildId.Value))
        {
            await RespondEphemeralAsync("You do not have permission to use this command.");
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        try
        {
            await voiceTimerService.StartAsync(guildId.Value);
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
        var guildId = Context.Interaction.GuildId;
        if (guildId is null)
        {
            await RespondEphemeralAsync("This command must be used in a server.");
            return;
        }

        if (!voiceTimerService.IsConfigured(guildId.Value))
        {
            await RespondEphemeralAsync("This server is not configured for the jungle timer.");
            return;
        }

        if (!await IsAuthorizedAsync(guildId.Value))
        {
            await RespondEphemeralAsync("You do not have permission to use this command.");
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        if (!voiceTimerService.IsRunning(guildId.Value))
        {
            await Context.Interaction.ModifyResponseAsync(m => m.WithContent("The timer is not running."));
            return;
        }

        try
        {
            await voiceTimerService.StopAsync(guildId.Value);
            await Context.Interaction.ModifyResponseAsync(m => m.WithContent("Timer stopped and bot disconnected."));
        }
        catch (Exception ex)
        {
            await Context.Interaction.ModifyResponseAsync(m =>
                m.WithContent($"Failed to stop the timer: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private async Task<bool> IsAuthorizedAsync(ulong guildId)
    {
        var guildSettings = options.Value.Servers.FirstOrDefault(s => s.GuildId == guildId);
        if (guildSettings is null) return false;
        var member = await restClient.GetGuildUserAsync(guildId, Context.User.Id);
        return member.RoleIds.Contains(guildSettings.AuthorizedRoleId);
    }

    private Task RespondEphemeralAsync(string content) =>
        Context.Interaction.SendResponseAsync(
            InteractionCallback.Message(new InteractionMessageProperties
            {
                Content = content,
                Flags = MessageFlags.Ephemeral
            }));
}
