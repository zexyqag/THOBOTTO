using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Modules;

public sealed class ModuleState(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    private readonly ConcurrentDictionary<(ulong GuildId, string Module), bool> _cache = new();

    public async ValueTask<bool> IsEnabledAsync(ulong guildId, string module)
    {
        if (_cache.TryGetValue((guildId, module), out var enabled))
            return enabled;

        await using var db = await dbFactory.CreateDbContextAsync();
        enabled = await db.EnabledModules.AnyAsync(m => m.GuildId == guildId && m.Module == module);
        return _cache[(guildId, module)] = enabled;
    }

    // Returns false if the module was already in that state.
    public async Task<bool> SetEnabledAsync(ulong guildId, string module, bool enabled, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var row = await db.EnabledModules.FindAsync(guildId, module);
        if (enabled == row is not null)
        {
            _cache[(guildId, module)] = enabled;
            return false;
        }

        if (enabled)
            db.EnabledModules.Add(new() { GuildId = guildId, Module = module });
        else
            db.EnabledModules.Remove(row!);

        db.AuditEntries.Add(new()
        {
            GuildId = guildId,
            ActorId = actorId,
            Action = enabled ? "module.enable" : "module.disable",
            Details = module,
            CreatedAt = time.GetUtcNow(),
        });

        await db.SaveChangesAsync();
        _cache[(guildId, module)] = enabled;
        return true;
    }
}
