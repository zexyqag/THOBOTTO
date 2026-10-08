using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// Records moderation cases: numbers them per server, tells the member, posts them to the
// moderation log and keeps that post up to date.
public sealed class CaseBook(
    IDbContextFactory<BotDbContext> dbFactory,
    RestClient rest,
    GatewayClient gateway,
    SettingsStore settings,
    TimeProvider time,
    ILogger<CaseBook> logger)
{
    public const string ModuleId = "moderation";

    private readonly SemaphoreSlim _numbering = new(1, 1);

    public DateTimeOffset Now => time.GetUtcNow();

    // Saves the case under the next number, then DMs the member (unless told not to) and logs it.
    // Returns whether the DM reached them.
    public async Task<(ModCase Case, bool Dmed)> OpenAsync(ModCase c, bool dm = true)
    {
        await _numbering.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            c.Number = 1 + (await db.ModCases.Where(x => x.GuildId == c.GuildId).MaxAsync(x => (int?)x.Number) ?? 0);
            db.ModCases.Add(c);
            db.AuditEntries.Add(new() { GuildId = c.GuildId, ActorId = c.ModeratorId, Action = $"mod.{c.Type}", Details = $"case {c.Number} on {c.TargetId}: {c.Reason}", CreatedAt = c.CreatedAt });
            await db.SaveChangesAsync();
        }
        finally
        {
            _numbering.Release();
        }

        var dmed = dm && await TellAsync(c);
        await PostLogAsync(c, await settings.GetAsync<ModRules>(c.GuildId, ModuleId));
        return (c, dmed);
    }

    // DMs the member about a case, if the server wants that. Before a kick or ban, while it still can.
    public async Task<bool> TellAsync(ModCase c)
    {
        var rules = await settings.GetAsync<ModRules>(c.GuildId, ModuleId);
        return rules.DmMembers && Describe.ToMember(c, rules.DmNamesModerator) is { } text && await DmAsync(c.GuildId, c.TargetId, text);
    }

    // Marks a member's lasting cases of a type as over (lifted, replaced or run out).
    public async Task EndAsync(ulong guildId, ulong targetId, string type, ulong? roleId = null)
    {
        List<ModCase> open;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            open = await db.ModCases.Where(c => c.GuildId == guildId && c.TargetId == targetId && c.Type == type && c.EndedAt == null && c.RoleId == roleId).ToListAsync();
            foreach (var c in open)
                c.EndedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
        }
        foreach (var c in open)
            await RefreshLogAsync(c);
    }

    // Lasting cases whose time is up.
    public async Task<IReadOnlyList<ModCase>> DueAsync()
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ModCases.AsNoTracking().Where(c => c.EndsAt <= now && c.EndedAt == null).ToListAsync();
    }

    public async Task<ModCase?> FindAsync(ulong guildId, int number)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ModCases.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId && c.Number == number);
    }

    public async Task<IReadOnlyList<ModCase>> HistoryAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ModCases.AsNoTracking().Where(c => c.GuildId == guildId && c.TargetId == userId).OrderByDescending(c => c.Number).ToListAsync();
    }

    // Warnings that still count: recent and not pardoned.
    public async Task<int> ActiveWarningsAsync(ulong guildId, ulong userId)
    {
        var rules = await settings.GetAsync<ModRules>(guildId, ModuleId);
        var since = time.GetUtcNow() - TimeSpan.FromDays(rules.WarningDays);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ModCases.CountAsync(c => c.GuildId == guildId && c.TargetId == userId && c.Type == CaseTypes.Warn && c.PardonedAt == null && c.CreatedAt >= since);
    }

    // Changes a case (reason, pardon, end), audits it and refreshes its log post.
    public async Task<ModCase?> ChangeAsync(ulong guildId, int number, ulong actorId, string what, Action<ModCase> change)
    {
        ModCase? c;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            c = await db.ModCases.FirstOrDefaultAsync(x => x.GuildId == guildId && x.Number == number);
            if (c is null)
                return null;
            change(c);
            db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "mod.case", Details = $"case {number}: {what}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        await RefreshLogAsync(c);
        return c;
    }

    private async Task PostLogAsync(ModCase c, ModRules rules)
    {
        if (rules.LogChannelId is not { } channelId)
            return;
        try
        {
            var message = await rest.SendMessageAsync(channelId, new()
            {
                Embeds = [Describe.Embed(c)],
                Components = Describe.LogButtons(c),
                AllowedMentions = AllowedMentionsProperties.None,
            });
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.ModCases.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LogChannelId, channelId)
                .SetProperty(x => x.LogMessageId, message.Id));
            (c.LogChannelId, c.LogMessageId) = (channelId, message.Id);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Posting case {Number} to the moderation log in {GuildId} failed: {Message}", c.Number, c.GuildId, ex.Message);
        }
    }

    private async Task RefreshLogAsync(ModCase c)
    {
        if (c is not { LogChannelId: { } channelId, LogMessageId: { } messageId })
            return;
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m =>
            {
                m.Embeds = [Describe.Embed(c)];
                m.Components = Describe.LogButtons(c);
            });
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
        }
    }

    private async Task<bool> DmAsync(ulong guildId, ulong userId, string text)
    {
        var server = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.Name : "a server";
        try
        {
            var dm = await rest.GetDMChannelAsync(userId);
            await rest.SendMessageAsync(dm.Id, new() { Content = $"**{server}**: {text}", AllowedMentions = AllowedMentionsProperties.None });
            return true;
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // DMs closed, or no shared server any more.
            return false;
        }
    }
}
