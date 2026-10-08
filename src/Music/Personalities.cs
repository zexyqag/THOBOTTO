using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Music;

// What each helper is called and says: its saved profile over its built-in character.
public sealed class Personalities(IDbContextFactory<BotDbContext> dbFactory)
{
    private readonly ConcurrentDictionary<ulong, HelperProfile?> _cache = new();

    public async Task<HelperProfile?> ProfileAsync(ulong helperId)
    {
        if (_cache.TryGetValue(helperId, out var cached))
            return cached;
        await using var db = await dbFactory.CreateDbContextAsync();
        return _cache[helperId] = await db.HelperProfiles.FindAsync(helperId);
    }

    public async Task SaveAsync(ulong helperId, Action<HelperProfile> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = await db.HelperProfiles.FindAsync(helperId);
        if (profile is null)
            db.HelperProfiles.Add(profile = new() { UserId = helperId });
        change(profile);
        // A changed dictionary isn't noticed on its own.
        db.Entry(profile).Property(p => p.Phrases).IsModified = true;
        await db.SaveChangesAsync();
        _cache[helperId] = profile;
    }

    public async Task<string> NicknameAsync(ulong helperId, int index)
        => (await ProfileAsync(helperId))?.Nickname ?? Character.For(index).Nickname;

    public async Task<int> ColorAsync(ulong helperId, int index)
        => (await ProfileAsync(helperId))?.Color ?? Character.For(index).Color;

    public async Task<IReadOnlyList<string>> PhrasesAsync(ulong helperId, int index, string moment)
        => (await ProfileAsync(helperId))?.Phrases.GetValueOrDefault(moment) is { Count: > 0 } own
            ? own
            : Character.For(index).Phrases.GetValueOrDefault(moment) ?? Character.Plain.Phrases[moment];

    public async Task<string> SayAsync(ulong helperId, int index, string moment, IReadOnlyDictionary<string, string> values)
    {
        var options = await PhrasesAsync(helperId, index, moment);
        var phrase = options[Random.Shared.Next(options.Count)];
        foreach (var (key, value) in values)
            phrase = phrase.Replace($"{{{key}}}", value);
        return phrase;
    }
}
