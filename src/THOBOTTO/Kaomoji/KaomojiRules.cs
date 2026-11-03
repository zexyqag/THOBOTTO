using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Kaomoji;

// Stored with SettingsStore under the module id.
public sealed record KaomojiRules
{
    [Setting("Post under the member's name", Help = "With their name and avatar, as if they wrote it (the bot needs Manage Webhooks). Off: as the bot's reply, which Discord heads with who used /kaomoji, linked to their account. In DMs it's always the bot's reply. Members can choose the bot's reply for theirs on their You page.")]
    public bool PostAsMember { get; init; } = true;

    [Setting("Add their username to the name", Help = "Posted under a nickname, the name reads like Yeeti (yeeti123): usernames can't be shared, so a nickname can't pass for someone else.")]
    public bool ShowUsername { get; init; } = true;

    [Setting("Fill in kao: words in messages", Help = "A message with kao:shrug or kao:table_flip in it is posted again under the member's name with the faces, and theirs removed (the bot needs Manage Messages too, and posting under the member's name on). They edit it with right-click → Apps → Edit my post. Members can turn it off for theirs on their You page.")]
    public bool InMessages { get; init; } = true;
}

// A person's own choices, the same in every server.
public sealed class KaomojiPreference
{
    public ulong UserId { get; init; }

    public bool PostAsMe { get; set; } = true;

    public bool InMessages { get; set; } = true;
}

public sealed class KaomojiPreferences(IDbContextFactory<BotDbContext> dbFactory)
{
    public async Task<KaomojiPreference> GetAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.KaomojiPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId) ?? new() { UserId = userId };
    }

    public async Task SetAsync(ulong userId, bool postAsMe, bool inMessages)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var preference = await db.KaomojiPreferences.FindAsync(userId);
        if (preference is null)
            db.KaomojiPreferences.Add(preference = new() { UserId = userId });
        (preference.PostAsMe, preference.InMessages) = (postAsMe, inMessages);
        await db.SaveChangesAsync();
    }
}

public sealed class KaomojiSummary(SettingsStore settings, GatewayClient gateway) : ISettingsSummary
{
    public string ModuleId => KaomojiShelf.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<KaomojiRules>(guildId, ModuleId);
        var allowed = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(gateway.Id, out var bot)
            ? bot.GetPermissions(guild)
            : Permissions.Administrator;
        bool Has(Permissions permission) => (allowed & (Permissions.Administrator | permission)) != 0;
        if (!rules.PostAsMember)
            return "Now: faces are posted as the bot's reply, and kao: words in messages are left as they are.";
        if (!Has(Permissions.ManageWebhooks))
            return "Now: faces are posted as the bot's reply, and kao: words in messages are left as they are, because the bot doesn't have Manage Webhooks in this server.";
        var name = rules.ShowUsername ? "the member's name (with their username beside a nickname)" : "the member's name";
        var words = !rules.InMessages ? "kao: words in messages are left as they are"
            : !Has(Permissions.ManageMessages) ? "kao: words in messages are left as they are, because the bot doesn't have Manage Messages in this server"
            : "kao: words in messages are filled in";
        return $"Now: faces are posted under {name} and avatar; {words}.";
    }
}
