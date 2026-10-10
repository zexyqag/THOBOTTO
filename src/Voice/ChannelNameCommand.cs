using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice;

public sealed class ChannelNameCommand(IDbContextFactory<BotDbContext> dbFactory, VoicePresence presence, ChannelNamer namer, ModuleState modules, AccessControl access)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private const int MaxName = 90;

    [SlashCommand("channel-name", "Name the voice channel a hub made you ({game} becomes what's played); leave out to go back", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> NameAsync(
        [SlashCommandParameter(Description = "Like \"{game} with the lads\"", MaxLength = MaxName)] string? name = null)
    {
        var guild = Context.Guild!;
        if (!await modules.IsEnabledAsync(guild.Id, DynamicVoice.ModuleId))
            return Replies.Ephemeral($"The `{DynamicVoice.ModuleId}` module is off.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!presence.Snapshot(guild.Id).TryGetValue(Context.User.Id, out var where)
            || await db.DynamicVoiceChannels.FindAsync(where.ChannelId) is not { } channel)
            return Replies.Ephemeral("Join the voice channel a hub made you first.");
        if (channel.OwnerId != Context.User.Id && !await access.CanAsync(guild, (GuildUser)Context.User, BotPermissions.ManageVoiceHubs))
            return Replies.Ephemeral($"Only <@{channel.OwnerId}>, whose channel it is, can name it.");

        channel.PinnedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await db.SaveChangesAsync();
        namer.RenameSoon(channel.ChannelId);
        return Replies.Ephemeral(channel.PinnedName is null
            ? "Back to automatic names."
            : $"It'll be called **{channel.PinnedName}** shortly (Discord allows two renames in ten minutes).");
    }
}
