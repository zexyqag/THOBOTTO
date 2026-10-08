using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

public sealed class MischiefCommands(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
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

        var rules = await settings.GetAsync<MischiefRules>(guild.Id, ModuleId);
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync();
        var history = await RenameHistory.ForTargetAsync(db, guild.Id, user.Id, now - TimeSpan.FromHours(rules.RenameWindowHours));
        if (history.LastAt is { } last && now - last < TimeSpan.FromMinutes(rules.RenameCooldownMinutes))
            return Replies.Ephemeral($"<@{user.Id}> was renamed <t:{last.ToUnixTimeSeconds()}:R>. They can be renamed again <t:{(last + TimeSpan.FromMinutes(rules.RenameCooldownMinutes)).ToUnixTimeSeconds()}:R>.");

        var price = await points.ChargesAsync(guild.Id) ? rules.RenamePrice(history.RecentCount) : 0;
        if (price > 0 && !await points.TrySpendAsync(guild.Id, actor.Id, price, $"rename {user.Id}"))
        {
            var currency = points.Rules(guild.Id);
            var balance = (await points.GetAsync(guild.Id, actor.Id))?.Balance ?? 0;
            return Replies.Ephemeral($"Renaming <@{user.Id}> costs {currency.Format(price)}; you have {currency.Format(balance)}.");
        }

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        try
        {
            await user.ModifyAsync(u => u.Nickname = name ?? "");
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            if (price > 0)
                await points.RefundAsync(guild.Id, actor.Id, price, $"rename {user.Id} refused by Discord");
            return Replies.Ephemeral($"Discord won't let me rename <@{user.Id}>: bots can't rename the owner, or anyone whose top role is above the bot's.");
        }

        db.Renames.Add(new()
        {
            GuildId = guild.Id,
            ActorId = actor.Id,
            TargetId = user.Id,
            OldName = user.Nickname,
            NewName = name,
            Reason = reason,
            Cost = price,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var what = name is null ? $"reset <@{user.Id}>'s nickname" : $"renamed <@{user.Id}> to **{name}**";
        var paid = price > 0 ? $" ({points.Rules(guild.Id).Format(price)})" : "";
        return new()
        {
            Content = $"<@{actor.Id}> {what}{(reason is null ? "" : $": {reason}")}{paid}",
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
