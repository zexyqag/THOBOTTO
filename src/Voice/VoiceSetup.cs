using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("voice-hubs", "Hub channels: joining one gives you a new voice channel (needs voice.hubs)")]
    [RequirePermission(BotPermissions.ManageVoiceHubs)]
    public sealed class VoiceHubSetup(IDbContextFactory<BotDbContext> dbFactory, ModuleState modules, TimeProvider time)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Interaction.GuildId!.Value;

        [SubSlashCommand("list", "Show the hub channels")]
        public async Task<InteractionMessageProperties> ListAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var hubs = await db.VoiceHubs.Where(h => h.GuildId == GuildId).Select(h => h.ChannelId).ToListAsync();

            return Replies.Ephemeral(hubs.Count == 0
                ? "There are no hub channels."
                : string.Join('\n', hubs.Select(id => $"<#{id}>")));
        }

        [SubSlashCommand("add", "Make a voice channel a hub")]
        public async Task<InteractionMessageProperties> AddAsync(
            [SlashCommandParameter(Description = "Voice channel", AllowedChannelTypes = [ChannelType.VoiceGuildChannel])] Channel channel)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.VoiceHubs.AnyAsync(h => h.ChannelId == channel.Id))
                return Replies.Ephemeral($"<#{channel.Id}> is already a hub.");

            db.VoiceHubs.Add(new() { ChannelId = channel.Id, GuildId = GuildId });
            Audit(db, "voice.hub.add", channel.Id);
            await db.SaveChangesAsync();

            var reply = $"<#{channel.Id}> is now a hub. New channels copy its category and permissions.";
            if (!await modules.IsEnabledAsync(GuildId, DynamicVoice.ModuleId))
                reply += $"\nThe `{DynamicVoice.ModuleId}` module is off; turn it on with `/setup modules enable`.";
            return Replies.Ephemeral(reply);
        }

        [SubSlashCommand("remove", "Stop a channel being a hub")]
        public async Task<InteractionMessageProperties> RemoveAsync(
            [SlashCommandParameter(Description = "Voice channel", AllowedChannelTypes = [ChannelType.VoiceGuildChannel])] Channel channel)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var hub = await db.VoiceHubs.FindAsync(channel.Id);
            if (hub is null || hub.GuildId != GuildId)
                return Replies.Ephemeral($"<#{channel.Id}> isn't a hub.");

            db.VoiceHubs.Remove(hub);
            Audit(db, "voice.hub.remove", channel.Id);
            await db.SaveChangesAsync();
            return Replies.Ephemeral($"<#{channel.Id}> is no longer a hub.");
        }

        private void Audit(BotDbContext db, string action, ulong channelId) => db.AuditEntries.Add(new()
        {
            GuildId = GuildId,
            ActorId = Context.User.Id,
            Action = action,
            Details = channelId.ToString(),
            CreatedAt = time.GetUtcNow(),
        });
    }
}
