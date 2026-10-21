using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Access;

// Per server: whether Discord's own permissions also count.
public sealed record AccessRules
{
    public bool FollowDiscord { get; init; }
}

// The bot's own permission model: permissions are granted to roles and the server owner has all
// of them. By default Discord's own permissions (Administrator included) don't count; a server can
// choose to follow them, where each bot permission comes with a Discord one (BotPermission.Discord).
public sealed class AccessControl(IDbContextFactory<BotDbContext> dbFactory, SettingsStore settings, TimeProvider time)
{
    public const string ModuleId = "perms";

    // Guild → (role, permission) grants.
    private readonly ConcurrentDictionary<ulong, IReadOnlySet<(ulong RoleId, string Permission)>> _grants = new();

    public ValueTask<bool> CanAsync(Guild guild, GuildUser user, string permission)
        => guild.OwnerId == user.Id ? ValueTask.FromResult(true) : RolesCanAsync(guild, user.RoleIds, permission);

    // Whether someone with just these roles (not the owner) may.
    public async ValueTask<bool> RolesCanAsync(Guild guild, IEnumerable<ulong> roleIds, string permission)
        => (await GetGrantsAsync(guild.Id)).Any(g => g.Permission == permission && roleIds.Contains(g.RoleId))
            || await FollowsDiscordAsync(guild.Id) && BotPermissions.Find(permission) is { } p && DiscordAllows(guild, roleIds, p.Discord);

    public async ValueTask<bool> FollowsDiscordAsync(ulong guildId) => (await settings.GetAsync<AccessRules>(guildId, ModuleId)).FollowDiscord;

    public Task SetFollowDiscordAsync(ulong guildId, bool follow, ulong actorId)
        => settings.SetAsync(guildId, ModuleId, new AccessRules { FollowDiscord = follow }, actorId, $"follow Discord's permissions: {follow}");

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
        if (guild.OwnerId == user.Id)
            return BotPermissions.All.Select(p => (p.Id, "server owner")).ToList();

        var granted = (await GetGrantsAsync(guild.Id))
            .Where(g => user.RoleIds.Contains(g.RoleId))
            .GroupBy(g => g.Permission)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => $"<@&{r.RoleId}>")));
        if (await FollowsDiscordAsync(guild.Id))
        {
            foreach (var p in BotPermissions.All.Where(p => DiscordAllows(guild, user, p.Discord)))
                granted[p.Id] = granted.TryGetValue(p.Id, out var roles) ? $"{roles}, Discord's {p.Discord}" : $"Discord's {p.Discord}";
        }
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
