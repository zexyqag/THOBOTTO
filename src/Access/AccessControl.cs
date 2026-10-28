using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Access;

// Per server: whether Discord's own permissions also count, and how long a sudo lasts.
public sealed record AccessRules
{
    public bool FollowDiscord { get; init; }

    public int SudoMinutes { get; init; } = 15;
}

// The bot's own permission model: permissions are granted to roles, and everyone, the server owner included, is
// what their roles say. The owner, and roles granted perms.sudo, can take every permission for a while (sudo), the
// explicit exception. By default Discord's own permissions (Administrator included) don't count; a server can
// choose to follow them, where each bot permission comes with a Discord one (BotPermission.Discord).
public sealed class AccessControl(IDbContextFactory<BotDbContext> dbFactory, SettingsStore settings, TimeProvider time)
{
    public const string ModuleId = "perms";

    // Guild → (role, permission) grants.
    private readonly ConcurrentDictionary<ulong, IReadOnlySet<(ulong RoleId, string Permission)>> _grants = new();

    // (server, member) → when their sudo ends.
    private readonly ConcurrentDictionary<(ulong Guild, ulong User), DateTimeOffset> _sudo = new();

    public ValueTask<bool> CanAsync(Guild guild, GuildUser user, string permission)
        => SudoUntil(guild.Id, user.Id) is not null ? ValueTask.FromResult(true) : RolesCanAsync(guild, user.RoleIds, permission);

    public DateTimeOffset? SudoUntil(ulong guildId, ulong userId)
        => _sudo.TryGetValue((guildId, userId), out var until) && until > time.GetUtcNow() ? until : null;

    // The owner always may; others with perms.sudo from their roles.
    public async ValueTask<bool> MaySudoAsync(Guild guild, GuildUser user)
        => guild.OwnerId == user.Id || await RolesCanAsync(guild, user.RoleIds, BotPermissions.Sudo);

    // Every permission for the member, for the server's sudo time; noted in the audit log.
    public async Task<DateTimeOffset> SudoAsync(Guild guild, GuildUser user)
    {
        var minutes = (await settings.GetAsync<AccessRules>(guild.Id, ModuleId)).SudoMinutes;
        var until = time.GetUtcNow() + TimeSpan.FromMinutes(minutes);
        _sudo[(guild.Id, user.Id)] = until;
        await NoteAsync(guild.Id, user.Id, "perms.sudo", $"on for {minutes} min");
        return until;
    }

    public async Task EndSudoAsync(ulong guildId, ulong userId)
    {
        if (_sudo.TryRemove((guildId, userId), out _))
            await NoteAsync(guildId, userId, "perms.sudo", "off");
    }

    public async Task<int> SudoMinutesAsync(ulong guildId) => (await settings.GetAsync<AccessRules>(guildId, ModuleId)).SudoMinutes;

    public async Task SetSudoMinutesAsync(ulong guildId, int minutes, ulong actorId)
    {
        var rules = await settings.GetAsync<AccessRules>(guildId, ModuleId);
        await settings.SetAsync(guildId, ModuleId, rules with { SudoMinutes = minutes }, actorId, $"sudo lasts {minutes} min");
    }

    private async Task NoteAsync(ulong guildId, ulong actorId, string action, string details)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = action, Details = details, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    // Whether someone with just these roles (not the owner) may.
    public async ValueTask<bool> RolesCanAsync(Guild guild, IEnumerable<ulong> roleIds, string permission)
        => (await GetGrantsAsync(guild.Id)).Any(g => g.Permission == permission && roleIds.Contains(g.RoleId))
            || await FollowsDiscordAsync(guild.Id) && BotPermissions.Find(permission) is { } p && DiscordAllows(guild, roleIds, p.Discord);

    public async ValueTask<bool> FollowsDiscordAsync(ulong guildId) => (await settings.GetAsync<AccessRules>(guildId, ModuleId)).FollowDiscord;

    public async Task SetFollowDiscordAsync(ulong guildId, bool follow, ulong actorId)
    {
        var rules = await settings.GetAsync<AccessRules>(guildId, ModuleId);
        await settings.SetAsync(guildId, ModuleId, rules with { FollowDiscord = follow }, actorId, $"follow Discord's permissions: {follow}");
    }

    // A member's server-wide Discord permissions, from their roles; Administrator includes everything.
    public static bool DiscordAllows(Guild guild, GuildUser user, Permissions permission) => DiscordAllows(guild, user.RoleIds, permission);

    public static bool DiscordAllows(Guild guild, IEnumerable<ulong> roleIds, Permissions permission)
    {
        var have = guild.Roles.TryGetValue(guild.Id, out var everyone) ? everyone.Permissions : 0;
        foreach (var roleId in roleIds)
        {
            if (guild.Roles.TryGetValue(roleId, out var role))
                have |= role.Permissions;
        }
        return (have & Permissions.Administrator) != 0 || (have & permission) == permission;
    }

    // Each permission the user has, with where it comes from.
    public async Task<IReadOnlyList<(string Permission, string Source)>> ExplainAsync(Guild guild, GuildUser user)
    {
        if (SudoUntil(guild.Id, user.Id) is { } until)
            return BotPermissions.All.Select(p => (p.Id, $"sudo until <t:{until.ToUnixTimeSeconds()}:t>")).ToList();

        var granted = (await GetGrantsAsync(guild.Id))
            .Where(g => user.RoleIds.Contains(g.RoleId))
            .GroupBy(g => g.Permission)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => $"<@&{r.RoleId}>")));
        if (await FollowsDiscordAsync(guild.Id))
        {
            foreach (var p in BotPermissions.All.Where(p => DiscordAllows(guild, user, p.Discord)))
                granted[p.Id] = granted.TryGetValue(p.Id, out var roles) ? $"{roles}, Discord's {p.Discord}" : $"Discord's {p.Discord}";
        }
        if (guild.OwnerId == user.Id)
            granted[BotPermissions.Sudo] = granted.TryGetValue(BotPermissions.Sudo, out var sources) ? $"{sources}, server owner" : "server owner";
        return granted.Select(g => (g.Key, g.Value)).OrderBy(p => p.Key).ToList();
    }

    public async Task<IReadOnlyList<(ulong RoleId, string Permission)>> ListAsync(ulong guildId)
        => (await GetGrantsAsync(guildId)).OrderBy(g => g.Permission).ThenBy(g => g.RoleId).ToList();

    // Returns false if nothing changed.
    public async Task<bool> SetAsync(ulong guildId, ulong roleId, string permission, bool granted, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.PermissionGrants.FindAsync(guildId, roleId, permission);
        if (granted == row is not null)
            return false;

        if (granted)
            db.PermissionGrants.Add(new() { GuildId = guildId, RoleId = roleId, Permission = permission });
        else
            db.PermissionGrants.Remove(row!);

        db.AuditEntries.Add(new()
        {
            GuildId = guildId,
            ActorId = actorId,
            Action = granted ? "perms.grant" : "perms.revoke",
            Details = $"{permission} {roleId}",
            CreatedAt = time.GetUtcNow(),
        });

        await db.SaveChangesAsync();
        _grants.TryRemove(guildId, out _);
        return true;
    }

    public async Task RemoveRoleAsync(ulong guildId, ulong roleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PermissionGrants.Where(g => g.GuildId == guildId && g.RoleId == roleId).ExecuteDeleteAsync();
        _grants.TryRemove(guildId, out _);
    }

    public static bool Outranks(Guild guild, GuildUser actor, GuildUser target, bool allowEqual)
        => Outranks(guild.OwnerId, (actor.Id, Rank(guild, actor)), (target.Id, Rank(guild, target)), allowEqual);

    // The owner outranks everyone; otherwise the higher rank, or the same one when allowed.
    public static bool Outranks<TRank>(ulong ownerId, (ulong Id, TRank Rank) actor, (ulong Id, TRank Rank) target, bool allowEqual)
        where TRank : IComparable<TRank>
    {
        if (ownerId == actor.Id)
            return true;
        if (ownerId == target.Id)
            return false;

        var compare = actor.Rank.CompareTo(target.Rank);
        return compare > 0 || (allowEqual && compare == 0);
    }

    // A member's rank is their highest role in Discord's role order; without roles, @everyone's.
    public static TRank Rank<TRank>(TRank everyone, IEnumerable<TRank> roles) where TRank : IComparable<TRank>
    {
        var rank = everyone;
        foreach (var role in roles)
        {
            if (role.CompareTo(rank) > 0)
                rank = role;
        }
        return rank;
    }

    // @everyone's id is the guild's.
    private static RolePosition Rank(Guild guild, GuildUser user)
        => Rank(guild.Roles[guild.Id].Position, user.RoleIds.Where(guild.Roles.ContainsKey).Select(id => guild.Roles[id].Position));

    private async ValueTask<IReadOnlySet<(ulong RoleId, string Permission)>> GetGrantsAsync(ulong guildId)
    {
        if (_grants.TryGetValue(guildId, out var grants))
            return grants;

        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.PermissionGrants.Where(g => g.GuildId == guildId).Select(g => new { g.RoleId, g.Permission }).ToListAsync();
        return _grants[guildId] = rows.Select(r => (r.RoleId, r.Permission)).ToHashSet();
    }
}
