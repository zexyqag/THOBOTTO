using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Mischief;

public sealed class MischiefCommands(ModuleState modules, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = "mischief";

    // House rule: anyone may rename anyone, whatever their rank, but never themselves.
    [SlashCommand("rename", "Rename someone (not yourself)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> RenameAsync(
        [SlashCommandParameter(Description = "Who to rename")] GuildUser user,
        [SlashCommandParameter(Description = "New nickname (leave out to reset it)", MaxLength = 32)] string? name = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 200)] string? reason = null)
    {
        var guild = Context.Guild!;
        var actor = (GuildUser)Context.User;

        if (!await modules.IsEnabledAsync(guild.Id, ModuleId))
            return Replies.Ephemeral($"The `{ModuleId}` module is off.");
        if (user.Id == actor.Id)
            return Replies.Ephemeral("You can't rename yourself. Ask someone else.");

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        try
        {
            await user.ModifyAsync(u => u.Nickname = name ?? "");
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return Replies.Ephemeral($"Discord won't let me rename <@{user.Id}>: bots can't rename the owner, or anyone whose top role is above the bot's.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.Renames.Add(new()
        {
            GuildId = guild.Id,
            ActorId = actor.Id,
            TargetId = user.Id,
            OldName = user.Nickname,
            NewName = name,
            Reason = reason,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var what = name is null ? $"reset <@{user.Id}>'s nickname" : $"renamed <@{user.Id}> to **{name}**";
        return new()
        {
            Content = $"<@{actor.Id}> {what}{(reason is null ? "." : $": {reason}")}",
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
