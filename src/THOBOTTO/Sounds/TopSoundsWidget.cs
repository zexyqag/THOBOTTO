using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Overview;

namespace THOBOTTO.Sounds;

// The soundboard's most played sounds lately, by helpers and from Discord's board alike.
public sealed class TopSoundsWidget(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time) : IWidgetKind
{
    public string Id => "sounds";

    public string Name => "Most played sounds";

    public string Description => "the soundboard's most played sounds lately";

    public IReadOnlyList<WidgetOption> Options => [new("count", "How many", "5", "1 to 15"), new("days", "Over the last", "30", "days, 1 to 365")];

    public TimeSpan Refresh => TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var days = WidgetOptions.Int(options, "days", 30, 1, 365);
        await using var db = await dbFactory.CreateDbContextAsync();
        var plays = await SoundBoard.PlaysSinceAsync(db, guildId, time.GetUtcNow().AddDays(-days));
        if (plays.Count == 0)
            return [];
        var ids = plays.Keys.ToList();
        var names = await db.Sounds.AsNoTracking().Where(s => ids.Contains(s.Id) && s.State == SoundStates.Library).ToDictionaryAsync(s => s.Id, s => s.Name);
        var top = plays.Where(p => names.ContainsKey(p.Key)).OrderByDescending(p => p.Value).ThenBy(p => names[p.Key], StringComparer.OrdinalIgnoreCase)
            .Take(WidgetOptions.Int(options, "count", 5, 1, 15)).ToList();
        if (top.Count == 0)
            return [];
        var lines = top.Select((p, i) => $"{i + 1}. 🔊 **{names[p.Key]}** · {p.Value} {(p.Value == 1 ? "play" : "plays")}");
        return [new($"Most played sounds, {days} {(days == 1 ? "day" : "days")}", string.Join('\n', lines))];
    }
}
