using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using VoiceTimerBot.Interfaces;

namespace VoiceTimerBot.Handlers;

[UsedImplicitly]
public class MessageCreateHandler(
    IVoiceTimerService voiceTimerService,
    IConfiguration configuration,
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
        }
    }

    private async Task HandleStartTimerAsync(Message message)
    {
        if (!await IsCouncilMemberAsync(message.GuildId, message.Author.Id))
        {
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent("You do not have permission to use this command."));
            return;
        }

        try
        {
            await voiceTimerService.StartAsync();
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent("Timer started."));
        }
        catch (Exception ex)
        {
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent($"Failed to start the timer: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private async Task HandleStopTimerAsync(Message message)
    {
        if (!await IsCouncilMemberAsync(message.GuildId, message.Author.Id))
        {
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent("You do not have permission to use this command."));
            return;
        }

        if (!voiceTimerService.IsRunning)
        {
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent("The timer is not running."));
            return;
        }

        try
        {
            await voiceTimerService.StopAsync();
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent("Timer stopped and bot disconnected."));
        }
        catch (Exception ex)
        {
            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties().WithContent($"Failed to stop the timer: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private async Task<bool> IsCouncilMemberAsync(ulong? guildId, ulong userId)
    {
        if (guildId is null) return false;
        var councilRoles = configuration.GetSection("CouncilRole").Get<ulong[]>() ?? [];
        var member = await restClient.GetGuildUserAsync(guildId.Value, userId);
        return member.RoleIds.Any(r => councilRoles.Contains(r));
    }
}
