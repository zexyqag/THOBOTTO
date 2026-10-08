using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

[SlashCommand("renames", "Rename history and prices", Contexts = [InteractionContextType.Guild])]
public sealed class RenamesCommands(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Interaction.GuildId!.Value;

    [SubSlashCommand("history", "Someone's past names, and why")]
    public async Task<InteractionMessageProperties> HistoryAsync(
        [SlashCommandParameter(Description = "Member (you if left out)")] GuildUser? user = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var userId = user?.Id ?? Context.User.Id;
        await using var db = await dbFactory.CreateDbContextAsync();
        var renames = await db.Renames
            .Where(r => r.GuildId == GuildId && r.TargetId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(15)
            .ToListAsync();

        if (renames.Count == 0)
            return Replies.Ephemeral($"<@{userId}> has never been renamed.");

        return Replies.Ephemeral($"Names of <@{userId}>, newest first:\n" + string.Join('\n', renames.Select(r =>
            $"<t:{r.CreatedAt.ToUnixTimeSeconds()}:d> **{r.NewName ?? "(reset)"}** by <@{r.ActorId}>{(r.Reason is null ? "" : $": {r.Reason}")}")));
    }

    [SubSlashCommand("top", "Most renamed in the last 30 days")]
    public async Task<InteractionMessageProperties> TopAsync()
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var since = time.GetUtcNow() - TimeSpan.FromDays(30);
        await using var db = await dbFactory.CreateDbContextAsync();
        var top = await db.Renames
            .Where(r => r.GuildId == GuildId && r.CreatedAt >= since)
            .GroupBy(r => r.TargetId)
            .Select(g => new { TargetId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToListAsync();

        return new()
        {
            Content = top.Count == 0
                ? "Nobody was renamed in the last 30 days."
                : "Most renamed, last 30 days:\n" + string.Join('\n', top.Select((t, i) => $"{i + 1}. <@{t.TargetId}>: {t.Count}×")),
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }

    [SubSlashCommand("price", "What renaming someone costs right now")]
    public async Task<InteractionMessageProperties> PriceAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser user)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if (!await points.ChargesAsync(GuildId))
            return Replies.Ephemeral("Renames are free here: the points module is off.");

        var rules = await settings.GetAsync<MischiefRules>(GuildId, MischiefCommands.ModuleId);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var history = await RenameHistory.ForTargetAsync(db, GuildId, user.Id, now - TimeSpan.FromHours(rules.RenameWindowHours));

        var text = $"Renaming <@{user.Id}> costs {points.Rules(GuildId).Format(rules.RenamePrice(history.RecentCount))}"
            + $" (renamed {history.RecentCount}× in the last {rules.RenameWindowHours:0.#} h).";
        if (history.LastAt is { } last && now - last < TimeSpan.FromMinutes(rules.RenameCooldownMinutes))
            text += $"\nCooling down until <t:{(last + TimeSpan.FromMinutes(rules.RenameCooldownMinutes)).ToUnixTimeSeconds()}:t>.";
        return Replies.Ephemeral(text);
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, MischiefCommands.ModuleId) ? null : Replies.Ephemeral($"The `{MischiefCommands.ModuleId}` module is off.");
}
