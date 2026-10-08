using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Points;

[SlashCommand("points", "Points earned by being active", Contexts = [InteractionContextType.Guild])]
public sealed partial class PointsCommands(PointsEngine points, ModuleState modules, IDbContextFactory<BotDbContext> dbFactory)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Interaction.GuildId!.Value;

    [SubSlashCommand("show", "Your balance and how fast you're earning (or someone else's)")]
    public async Task<InteractionMessageProperties> ShowAsync(
        [SlashCommandParameter(Description = "Member (you if left out)")] GuildUser? user = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var userId = user?.Id ?? Context.User.Id;
        var rules = points.Rules(GuildId);
        if (await points.GetAsync(GuildId, userId) is not { } s)
            return Replies.Ephemeral($"<@{userId}> hasn't earned any {rules.CurrencyName} yet.");

        return Replies.Ephemeral($"""
            <@{userId}> has **{rules.Format(s.Balance)}**.
            Earning {s.PerMinute:0.00} per minute at activity level {s.Level:0.00}: voice {s.Voice:0.00}, chat {s.Chat:0.00}, reactions received {s.Received:0.00}, given {s.Given:0.00}.
            """);
    }

    [SubSlashCommand("top", "The leaderboard")]
    public async Task<InteractionMessageProperties> TopAsync()
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var rules = points.Rules(GuildId);
        var top = await points.TopAsync(GuildId, 10);
        var text = top.Count == 0
            ? $"Nobody has earned any {rules.CurrencyName} yet."
            : string.Join('\n', top.Select((s, i) => $"{i + 1}. <@{s.UserId}>: {rules.Format(s.Balance)}"));

        return new()
        {
            Content = text,
            AllowedMentions = AllowedMentionsProperties.None,
            Flags = rules.LeaderboardPublic ? null : MessageFlags.Ephemeral,
        };
    }

    [SubSlashCommand("history", "Your recent point changes")]
    public async Task<InteractionMessageProperties> HistoryAsync()
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var rules = points.Rules(GuildId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.PointEntries
            .Where(e => e.GuildId == GuildId && e.UserId == Context.User.Id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(15)
            .ToListAsync();

        if (entries.Count == 0)
            return Replies.Ephemeral("Nothing yet. Activity is written down once an hour.");

        return Replies.Ephemeral(string.Join('\n', entries.Select(e =>
            $"<t:{e.CreatedAt.ToUnixTimeSeconds()}:f> {e.Amount:+0.##;-0.##} {rules.CurrencyName}, {Describe(e)}")));
    }

    [SubSlashCommand("adjust", "Add or take points, with a reason")]
    [RequirePermission(BotPermissions.ManagePoints)]
    public async Task<InteractionMessageProperties> AdjustAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser user,
        [SlashCommandParameter(Description = "Amount (negative takes points)")] double amount,
        [SlashCommandParameter(Description = "Why", MaxLength = 200)] string reason)
    {
        var applied = await points.AdjustAsync(GuildId, user.Id, amount, reason, Context.User.Id);
        var rules = points.Rules(GuildId);
        return Replies.Ephemeral(applied == amount
            ? $"Done: {amount:+0.##;-0.##} {rules.CurrencyName} for <@{user.Id}>."
            : $"Applied {applied:+0.##;-0.##} {rules.CurrencyName} for <@{user.Id}>: balances can't go below zero here.");
    }

    private static string Describe(PointEntry e)
    {
        var what = e.Kind == PointEntryKinds.Kudos && e.ActorId is { } other
            ? $"kudos {(e.Amount < 0 ? "to" : "from")} <@{other}>"
            : e.Kind;
        return e.Reason is null ? what : $"{what}: {e.Reason}";
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, PointsEngine.ModuleId) ? null : Replies.Ephemeral($"The `{PointsEngine.ModuleId}` module is off.");
}
