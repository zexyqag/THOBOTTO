using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Reposts;

// Stored with SettingsStore under the module id.
public sealed record RepostRules
{
    [Setting("Add their username to the name", Help = "Posted under a nickname or a character, the name reads like Yeeti (yeeti123): usernames can't be shared, so nobody can pass for someone else.")]
    public bool ShowUsername { get; init; } = true;

    [Setting("Not in these channels", Help = "Messages there are left as written.", Kind = SettingKind.TextChannel)]
    public IReadOnlyList<ulong> ExcludedChannelIds { get; init; } = [];
}

// A person's own choices about what of theirs is posted again, the same in every server.
public sealed class PostingPreference
{
    public ulong UserId { get; init; }

    // /kaomoji under their name; off, as the bot's reply.
    public bool KaomojiAsMe { get; set; } = true;

    public bool KaoWords { get; set; } = true;

    public bool EmojiNames { get; set; } = true;
}

public sealed class PostingPreferences(IDbContextFactory<BotDbContext> dbFactory)
{
    public async Task<PostingPreference> GetAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PostingPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId) ?? new() { UserId = userId };
    }

    public async Task SetAsync(ulong userId, Action<PostingPreference> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var preference = await db.PostingPreferences.FindAsync(userId);
        if (preference is null)
            db.PostingPreferences.Add(preference = new() { UserId = userId });
        change(preference);
        await db.SaveChangesAsync();
    }
}

public sealed class RepostSummary(SettingsStore settings, GatewayClient gateway) : ISettingsSummary
{
    public string ModuleId => Reposter.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<RepostRules>(guildId, ModuleId);
        var allowed = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(gateway.Id, out var bot)
            ? bot.GetPermissions(guild)
            : Permissions.Administrator;
        var missing = string.Join(" and ", new[] { (Permissions.ManageWebhooks, "Manage Webhooks"), (Permissions.ManageMessages, "Manage Messages") }
            .Where(p => (allowed & (Permissions.Administrator | p.Item1)) == 0)
            .Select(p => p.Item2));
        if (missing.Length > 0)
            return $"Now: nothing is posted under members' names, because the bot doesn't have {missing} in this server.";
        var name = rules.ShowUsername ? "under the member's name, with their username beside a nickname or a character" : "under the member's name";
        var not = rules.ExcludedChannelIds.Count == 0 ? "" : $", except in {string.Join(", ", rules.ExcludedChannelIds.Select(channel))}";
        return $"Now: messages are posted again {name}{not}.";
    }
}
