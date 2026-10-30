using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Modules;

public sealed class ModuleState(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, DiscordApp app)
{
    private readonly ConcurrentDictionary<(ulong GuildId, string Module), bool> _cache = new();

    // Why a module can't be turned on with this bot (a privileged intent the Developer Portal doesn't allow it), or null.
    public string? Needs(string module)
        => ModuleRegistry.Find(module) is { NeedsMembers: true } needy && !app.Members
            ? $"The {needy.Name} module needs the Server Members intent, which is off for this bot: turn it on in the Developer Portal (Bot → Privileged Gateway Intents), then restart the bot."
            : null;

    public async ValueTask<bool> IsEnabledAsync(ulong guildId, string module)
    {
        if (_cache.TryGetValue((guildId, module), out var enabled))
            return enabled;

        await using var db = await dbFactory.CreateDbContextAsync();
        enabled = await db.EnabledModules.AnyAsync(m => m.GuildId == guildId && m.Module == module);
        return _cache[(guildId, module)] = enabled;
    }

    // Returns false if the module was already in that state, or can't be on with this bot.
    public async Task<bool> SetEnabledAsync(ulong guildId, string module, bool enabled, ulong actorId)
    {
        if (enabled && Needs(module) is not null)
            return false;
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
