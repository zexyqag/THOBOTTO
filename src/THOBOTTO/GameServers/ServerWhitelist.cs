using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Members;
using THOBOTTO.Steam;

namespace THOBOTTO.GameServers;

// Members putting themselves on game servers' whitelists through the console, and taken off when they leave or lose
// the role; a removal the game server couldn't take is tried again each hour. The commands aren't logged: Zomboid's
// carries the member's password.
public sealed class ServerWhitelist(IDbContextFactory<BotDbContext> dbFactory, ServerControl control, SteamLibrary steam, TimeProvider time, ILogger<ServerWhitelist> logger)
    : BackgroundService, IMemberLeft
{
    private static readonly TimeSpan Retry = TimeSpan.FromHours(1);

    // Members with entries, so everyone else's role changes need nothing looked up.
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), byte> _listed = new();

    // Whether it has the button: open, its console set up, and a game whose whitelist the bot knows.
    public static bool Open(GameServer server)
        => server.WhitelistFor != WhitelistOpen.Off && server.RconAddress is not null && GameControls.Whitelist(server.Game) is not null;

    // Why this member can't, or null.
    public static string? Refused(GameServer server, IEnumerable<ulong> roles)
        => !Open(server) ? "That game server's whitelist isn't open through the bot."
            : server.WhitelistFor == WhitelistOpen.Role && !(server.WhitelistRoleId is { } role && roles.Contains(role)) ? $"That whitelist is for <@&{server.WhitelistRoleId}>."
            : null;

    public async Task<WhitelistEntry?> FindAsync(long serverId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WhitelistEntries.AsNoTracking().FirstOrDefaultAsync(w => w.ServerId == serverId && w.UserId == userId);
    }

    public async Task<IReadOnlyList<WhitelistEntry>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WhitelistEntries.AsNoTracking().Where(w => w.GuildId == guildId).OrderBy(w => w.AddedAt).ToListAsync();
    }

    // What the member is told. name as they gave it (not for Steam), password for an account.
    public async Task<string> AddAsync(GameServer server, ulong userId, string? name, string? password)
    {
        var whitelist = GameControls.Whitelist(server.Game)!;
        if (whitelist.By == WhitelistBy.Steam)
        {
            if (await steam.FindAsync(userId) is not { } link)
                return "This server whitelists your Steam account: link it with `/steam link`, then press the button again.";
            name = $"Steam_{link.SteamId}";
        }
        name = name?.Trim() ?? "";
        if (name.Length == 0 || whitelist.Valid is { } valid && !Regex.IsMatch(name, valid))
            return whitelist.By == WhitelistBy.Name ? "That isn't a Minecraft name: 3 to 16 letters, digits and underscores." : "Names can't have spaces or quotes, and at most 32 characters.";
        if (whitelist.By == WhitelistBy.Account && (password is null || !Regex.IsMatch(password, whitelist.Valid!)))
            return "The password can't have spaces or quotes, and at most 32 characters.";
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.WhitelistEntries.FirstOrDefaultAsync(w => w.ServerId == server.Id && w.UserId == userId);
        if (entry is { Removing: false } && entry.Name == name && whitelist.By != WhitelistBy.Account)
            return $"You're on it already, as **{name}**.";
        // Another name than before: the old one comes off first.
        if (entry is not null && entry.Name != name && (await control.RunAsync(server, whitelist.Remove(entry.Name))).Problem is { } stuck)
            return $"Your old name couldn't be taken off first: {stuck}";
        var (answer, problem) = await control.RunAsync(server, whitelist.Add(name, password));
        if (problem is not null)
            return $"The game server couldn't be asked: {problem}";
        if (!whitelist.Took(answer ?? ""))
            return $"The game server said: {Clip(answer)}";
        if (entry is null)
            db.WhitelistEntries.Add(entry = new() { ServerId = server.Id, GuildId = server.GuildId, UserId = userId, Name = name });
        (entry.Name, entry.AddedAt, entry.Removing) = (name, time.GetUtcNow(), false);
        await db.SaveChangesAsync();
        _listed[(server.GuildId, userId)] = 0;
        using (logger.BeginScope(DebugLog.Guild(server.GuildId)))
            logger.LogInformation("<@{UserId}> is on {Server}'s whitelist as {Name}", userId, server.Name ?? server.Address, name);
        var shown = server.Name ?? server.Address;
        return whitelist.By switch
        {
            WhitelistBy.Steam => $"You're on **{shown}**'s whitelist with your Steam account.",
            WhitelistBy.Account => $"You're on **{shown}**'s whitelist: log in as **{name}** with the password you chose.",
            _ => $"You're on **{shown}**'s whitelist as **{name}**.",
        };
    }

    // Off the whitelist (by someone running it, or as its member left or lost the role); what the game server can't
    // take now is tried again later. Null when it's off, else why not yet.
    public async Task<string?> RemoveAsync(long entryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.WhitelistEntries.FindAsync(entryId) is not { } entry)
            return null;
        if (await db.GameServers.FindAsync(entry.ServerId) is { RconAddress: not null } server && GameControls.Whitelist(server.Game) is { } whitelist
            && (await control.RunAsync(server, whitelist.Remove(entry.Name))).Problem is { } problem)
        {
            entry.Removing = true;
            await db.SaveChangesAsync();
            using (logger.BeginScope(DebugLog.Guild(entry.GuildId)))
                logger.LogWarning("{Name} couldn't be taken off {Server}'s whitelist yet: {Problem}", entry.Name, server.Name ?? server.Address, problem);
            return problem;
        }
        db.WhitelistEntries.Remove(entry);
        await db.SaveChangesAsync();
        if (!await db.WhitelistEntries.AnyAsync(w => w.GuildId == entry.GuildId && w.UserId == entry.UserId))
            _listed.TryRemove((entry.GuildId, entry.UserId), out _);
        return null;
    }

    public async Task MemberLeftAsync(ulong guildId, ulong userId)
    {
        foreach (var id in await EntriesAsync(guildId, userId, _ => true))
            await RemoveAsync(id);
    }

    // Off the whitelists that need a role they no longer have.
    public async Task RolesChangedAsync(ulong guildId, ulong userId, IReadOnlyCollection<ulong> roles)
    {
        if (!_listed.ContainsKey((guildId, userId)))
            return;
        foreach (var id in await EntriesAsync(guildId, userId, s => s.WhitelistFor == WhitelistOpen.Role && !(s.WhitelistRoleId is { } role && roles.Contains(role))))
            await RemoveAsync(id);
    }

    private async Task<IReadOnlyList<long>> EntriesAsync(ulong guildId, ulong userId, Func<GameServer, bool> off)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.WhitelistEntries.AsNoTracking().Where(w => w.GuildId == guildId && w.UserId == userId)
            .Join(db.GameServers, w => w.ServerId, s => s.Id, (w, s) => new { w.Id, Server = s }).ToListAsync();
        return [.. entries.Where(e => off(e.Server)).Select(e => e.Id)];
    }

    private static string Clip(string? answer) => answer?.Trim() is { Length: > 0 } text ? text[..Math.Min(text.Length, 300)] : "(nothing)";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            foreach (var w in await db.WhitelistEntries.Select(w => new { w.GuildId, w.UserId }).ToListAsync(ct))
                _listed[(w.GuildId, w.UserId)] = 0;
        using var timer = new PeriodicTimer(Retry, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                List<long> stuck;
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                    stuck = await db.WhitelistEntries.Where(w => w.Removing).Select(w => w.Id).ToListAsync(ct);
                foreach (var id in stuck)
                    await RemoveAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Taking members off game servers' whitelists again failed");
            }
        }
    }
}
