using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

[SlashCommand("mod", "Moderation", Contexts = [InteractionContextType.Guild])]
public sealed class ModCommands(ModuleState modules, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = "moderation";

    [SubSlashCommand("nick", "Change or reset a member's nickname")]
    [RequirePermission(BotPermissions.ModerateNicknames)]
    public async Task<InteractionMessageProperties> NickAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser user,
        [SlashCommandParameter(Description = "New nickname (leave out to reset it)", MaxLength = 32)] string? name = null)
    {
        var guild = Context.Guild!;
        var actor = (GuildUser)Context.User;

        if (!await modules.IsEnabledAsync(guild.Id, ModuleId))
            return Replies.Ephemeral($"The `{ModuleId}` module is off.");
        if (user.Id == actor.Id)
            return Replies.Ephemeral("Not on yourself.");
        if (!AccessControl.Outranks(guild, actor, user, allowEqual: false))
            return Replies.Ephemeral($"<@{user.Id}> doesn't rank below you.");

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        try
        {
            await user.ModifyAsync(u => u.Nickname = name ?? "");
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return Replies.Ephemeral($"Discord won't let me rename <@{user.Id}>: bots can't rename the owner, or anyone whose top role is above the bot's.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditEntries.Add(new()
        {
            GuildId = guild.Id,
            ActorId = actor.Id,
            Action = "mod.nick",
            Details = $"{user.Id} {user.Nickname ?? "(none)"} -> {name ?? "(none)"}",
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        return Replies.Ephemeral(name is null ? $"Reset <@{user.Id}>'s nickname." : $"Renamed <@{user.Id}> to **{name}**.");
    }
}
