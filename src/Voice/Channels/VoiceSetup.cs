using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Voice.Channels;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("voice-hubs", "Hub channels: joining one gives you a new voice channel (needs " + BotPermissions.ManageVoiceChannels + ")")]
    [RequirePermission(BotPermissions.ManageVoiceChannels)]
    public sealed class VoiceHubSetup(SettingsStore settings, ModuleState modules) : GuildCommandModule
    {
        [SubSlashCommand("list", "Show the hub channels")]
        public async Task<InteractionMessageProperties> ListAsync()
        {
            var hubs = (await settings.GetAsync<VoiceRules>(GuildId, DynamicVoice.ModuleId)).HubChannelIds;
            return Replies.Ephemeral(hubs.Count == 0
                ? "There are no hub channels."
                : string.Join('\n', hubs.Select(id => $"<#{id}>")));
        }

        [SubSlashCommand("add", "Make a voice channel a hub")]
        public async Task<InteractionMessageProperties> AddAsync(
            [SlashCommandParameter(Description = "Voice channel", AllowedChannelTypes = [ChannelType.VoiceGuildChannel])] Channel channel)
        {
            var rules = await settings.GetAsync<VoiceRules>(GuildId, DynamicVoice.ModuleId);
            if (rules.HubChannelIds.Contains(channel.Id))
                return Replies.Ephemeral($"<#{channel.Id}> is already a hub.");
            await settings.SetAsync(GuildId, DynamicVoice.ModuleId, rules with { HubChannelIds = [.. rules.HubChannelIds, channel.Id] }, Context.User.Id, $"hub added: {channel.Id}");

            var reply = $"<#{channel.Id}> is now a hub. New channels copy its category and permissions.";
            if (!await modules.IsEnabledAsync(GuildId, DynamicVoice.ModuleId))
                reply += "\n" + ModuleRegistry.OffAndHowToTurnOn(DynamicVoice.ModuleId);
            return Replies.Ephemeral(reply);
        }

        [SubSlashCommand("remove", "Stop a channel being a hub")]
        public async Task<InteractionMessageProperties> RemoveAsync(
            [SlashCommandParameter(Description = "Voice channel", AllowedChannelTypes = [ChannelType.VoiceGuildChannel])] Channel channel)
        {
            var rules = await settings.GetAsync<VoiceRules>(GuildId, DynamicVoice.ModuleId);
            if (!rules.HubChannelIds.Contains(channel.Id))
                return Replies.Ephemeral($"<#{channel.Id}> isn't a hub.");
            await settings.SetAsync(GuildId, DynamicVoice.ModuleId, rules with { HubChannelIds = rules.HubChannelIds.Where(h => h != channel.Id).ToList() }, Context.User.Id, $"hub removed: {channel.Id}");
            return Replies.Ephemeral($"<#{channel.Id}> is no longer a hub.");
        }
    }
}
