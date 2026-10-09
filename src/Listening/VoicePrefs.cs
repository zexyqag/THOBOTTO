using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Listening;

// Members' voice preferences, read often (every few seconds) so kept in memory and reloaded now and then.
public sealed class VoicePrefs(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    private Dictionary<(ulong Guild, ulong User), VoicePreference> _all = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public async Task<VoicePreference> GetAsync(ulong guildId, ulong userId)
        => (await AllAsync()).GetValueOrDefault((guildId, userId)) ?? new() { GuildId = guildId, UserId = userId };

    public async Task<IReadOnlyDictionary<(ulong Guild, ulong User), VoicePreference>> AllAsync()
    {
        if (time.GetUtcNow() - _loadedAt > Fresh)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            _all = await db.VoicePreferences.AsNoTracking().ToDictionaryAsync(p => (p.GuildId, p.UserId));
            _loadedAt = time.GetUtcNow();
        }
        return _all;
    }

    public async Task SetAsync(ulong guildId, ulong userId, Action<VoicePreference> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var preference = await db.VoicePreferences.FindAsync(guildId, userId);
        if (preference is null)
            db.VoicePreferences.Add(preference = new() { GuildId = guildId, UserId = userId });
        change(preference);
        if (!preference.Listen && !preference.AutoListen && !preference.AutoMusic)
            db.VoicePreferences.Remove(preference);
        await db.SaveChangesAsync();
        _loadedAt = DateTimeOffset.MinValue;
    }
}
