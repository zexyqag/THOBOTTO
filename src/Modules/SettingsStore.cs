using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Modules;

// Reads and writes ModuleSettings. Settings are immutable records; a guild without a row gets the defaults.
public sealed class SettingsStore(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    private readonly ConcurrentDictionary<(ulong GuildId, string Module), object> _cache = new();

    public async ValueTask<T> GetAsync<T>(ulong guildId, string module) where T : class, new()
    {
        if (_cache.TryGetValue((guildId, module), out var cached))
            return (T)cached;

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ModuleSettings.FindAsync(guildId, module);
        var value = row is null ? new T() : JsonSerializer.Deserialize<T>(row.Json, JsonSerializerOptions.Web)!;
        _cache[(guildId, module)] = value;
        return value;
    }

    // Every guild's settings for a module that has a row.
    public async Task<IReadOnlyDictionary<ulong, T>> GetAllAsync<T>(string module) where T : class
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.ModuleSettings.Where(s => s.Module == module).ToListAsync();
        return rows.ToDictionary(r => r.GuildId, r => JsonSerializer.Deserialize<T>(r.Json, JsonSerializerOptions.Web)!);
    }

    public async Task SetAsync<T>(ulong guildId, string module, T value, ulong actorId, string details) where T : class
    {
        var json = JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ModuleSettings.FindAsync(guildId, module);
        if (row is null)
            db.ModuleSettings.Add(new() { GuildId = guildId, Module = module, Json = json });
        else
            row.Json = json;

        db.AuditEntries.Add(new()
        {
            GuildId = guildId,
            ActorId = actorId,
            Action = $"{module}.settings",
            Details = details,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        _cache[(guildId, module)] = value;
    }

    // "Name=value" for each property that differs, for the audit log.
    public static string Diff<T>(T before, T after)
        => string.Join(' ', typeof(T).GetProperties()
            .Where(p => !Equals(p.GetValue(before), p.GetValue(after)))
            .Select(p => $"{p.Name}={p.GetValue(after)}"));
}
