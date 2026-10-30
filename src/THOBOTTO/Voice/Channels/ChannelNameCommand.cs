using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Channels;

public sealed class ChannelNameCommand(IDbContextFactory<BotDbContext> dbFactory, VoicePresence presence, ChannelNamer namer, ModuleState modules, AccessControl access)
    : GuildCommandModule
{
    private const int MaxName = 90;

    [SlashCommand("channel-name", "Name the voice channel a hub made you ({game} becomes what's played); leave out to go back", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> NameAsync(
        [SlashCommandParameter(Description = "Like \"{game} with the lads\"", MaxLength = MaxName)] string? name = null)
    {
        if (!await modules.IsEnabledAsync(GuildId, DynamicVoice.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(DynamicVoice.ModuleId));
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!presence.Snapshot(GuildId).TryGetValue(Context.User.Id, out var where)
            || await db.DynamicVoiceChannels.FindAsync(where.ChannelId) is not { } channel)
            return Replies.Ephemeral("Join the voice channel a hub made you first.");
        if (channel.OwnerId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageVoiceChannels))
            return Replies.Ephemeral($"Only <@{channel.OwnerId}>, whose channel it is, can name it.");

        channel.PinnedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await db.SaveChangesAsync();
        namer.RenameSoon(channel.ChannelId);
        return Replies.Ephemeral(channel.PinnedName is null
            ? "Back to automatic names."
            : $"It'll be called **{channel.PinnedName}** shortly (Discord allows two renames in ten minutes).");
    }
}
