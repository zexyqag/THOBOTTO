using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Points;

public sealed class KudosCommands(PointsEngine points, ModuleState modules, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("kudos", "Give some of your points to someone", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> KudosAsync(
        [SlashCommandParameter(Description = "Who to thank")] GuildUser user,
        [SlashCommandParameter(Description = "How many points", MinValue = 1, MaxValue = 1_000_000)] int amount,
        [SlashCommandParameter(Description = "What for", MaxLength = 200)] string? reason = null)
    {
        var guildId = Context.Guild!.Id;
        var giverId = Context.User.Id;

        if (!await modules.IsEnabledAsync(guildId, PointsEngine.ModuleId))
            return Replies.Ephemeral($"The `{PointsEngine.ModuleId}` module is off.");
        if (user.Id == giverId)
            return Replies.Ephemeral("Kudos are for someone else.");
        if (user.IsBot)
            return Replies.Ephemeral("Bots don't need points.");

        var rules = points.Rules(guildId);
        var since = time.GetUtcNow() - TimeSpan.FromDays(1);
        await using var db = await dbFactory.CreateDbContextAsync();
        var given = -await db.PointEntries
            .Where(e => e.GuildId == guildId && e.UserId == giverId && e.Kind == PointEntryKinds.Kudos && e.Amount < 0 && e.CreatedAt >= since)
            .SumAsync(e => e.Amount);
        if (given + amount > rules.KudosDailyLimit)
            return Replies.Ephemeral($"You can give {rules.Format(rules.KudosDailyLimit)} a day; you have {rules.Format(Math.Max(0, rules.KudosDailyLimit - given))} left.");

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (!await points.TransferAsync(guildId, giverId, user.Id, amount, reason))
        {
            var balance = (await points.GetAsync(guildId, giverId))?.Balance ?? 0;
            return Replies.Ephemeral($"You can't give {rules.Format(amount)}: you have {rules.Format(balance)}.");
        }

        return new()
        {
            Content = $"<@{giverId}> gave <@{user.Id}> {rules.Format(amount)}{(reason is null ? "." : $": {reason}")}",
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
