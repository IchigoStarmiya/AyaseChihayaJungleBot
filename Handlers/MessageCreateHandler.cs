using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using VoiceTimerBot.Interfaces;
using VoiceTimerBot.Services;

namespace VoiceTimerBot.Handlers;

[UsedImplicitly]
public class MessageCreateHandler(
    IVoiceTimerService voiceTimerService,
    IOptions<VoiceTimerSettings> options,
    RestClient restClient)
    : IMessageCreateGatewayHandler
{
    private const string Prefix = ";";

    public async ValueTask HandleAsync(Message message)
    {
        if (message.Author.IsBot) return;
        if (!message.Content.StartsWith(Prefix, StringComparison.Ordinal)) return;

        var commandText = message.Content[Prefix.Length..].Trim();
        var spaceIndex = commandText.IndexOf(' ');
        var commandName = spaceIndex >= 0 ? commandText[..spaceIndex] : commandText;

        switch (commandName.ToLowerInvariant())
        {
            case "starttimer":
                await HandleStartTimerAsync(message);
                break;
            case "stoptimer":
                await HandleStopTimerAsync(message);
                break;
            case "leaveserver":
                await HandleLeaveServerAsync(message, commandText, spaceIndex);
                break;
        }
    }

    private async Task HandleLeaveServerAsync(Message message, string commandText, int spaceIndex)
    {
        if (message.Author.Id != options.Value.OwnerId)
        {
            await SendAsync(message.ChannelId, "You do not have permission to use this command.");
            return;
        }

        var argument = spaceIndex >= 0 ? commandText[(spaceIndex + 1)..].Trim() : string.Empty;
        if (!ulong.TryParse(argument, out var targetGuildId))
        {
            await SendAsync(message.ChannelId, "Usage: ;leaveserver <guildId>");
            return;
        }

        try
        {
            await restClient.LeaveGuildAsync(targetGuildId);
            await SendAsync(message.ChannelId, $"Left guild {targetGuildId}.");
        }
        catch (Exception ex)
        {
            await SendAsync(message.ChannelId, $"Failed to leave guild {targetGuildId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleStartTimerAsync(Message message)
    {
        if (message.GuildId is null) return;
        var guildId = message.GuildId.Value;

        if (!voiceTimerService.IsConfigured(guildId))
        {
            await SendAsync(message.ChannelId, "This server is not configured for the jungle timer.");
            return;
        }

        if (!await IsAuthorizedAsync(guildId, message.Author.Id))
        {
            await SendAsync(message.ChannelId, "You do not have permission to use this command.");
            return;
        }

        try
        {
            await voiceTimerService.StartAsync(guildId);
            await SendAsync(message.ChannelId, "Timer started.");
        }
        catch (Exception ex)
        {
            await SendAsync(message.ChannelId, $"Failed to start the timer: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleStopTimerAsync(Message message)
    {
        if (message.GuildId is null) return;
        var guildId = message.GuildId.Value;

        if (!voiceTimerService.IsConfigured(guildId))
        {
            await SendAsync(message.ChannelId, "This server is not configured for the jungle timer.");
            return;
        }

        if (!await IsAuthorizedAsync(guildId, message.Author.Id))
        {
            await SendAsync(message.ChannelId, "You do not have permission to use this command.");
            return;
        }

        if (!voiceTimerService.IsRunning(guildId))
        {
            await SendAsync(message.ChannelId, "The timer is not running.");
            return;
        }

        try
        {
            await voiceTimerService.StopAsync(guildId);
            await SendAsync(message.ChannelId, "Timer stopped and bot disconnected.");
        }
        catch (Exception ex)
        {
            await SendAsync(message.ChannelId, $"Failed to stop the timer: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<bool> IsAuthorizedAsync(ulong guildId, ulong userId)
    {
        var guildSettings = options.Value.Servers.FirstOrDefault(s => s.GuildId == guildId);
        if (guildSettings is null) return false;
        var member = await restClient.GetGuildUserAsync(guildId, userId);
        return member.RoleIds.Contains(guildSettings.AuthorizedRoleId);
    }

    private Task SendAsync(ulong channelId, string content) =>
        restClient.SendMessageAsync(channelId, new MessageProperties().WithContent(content));
}
