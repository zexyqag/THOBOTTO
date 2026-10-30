using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Happenings;
using THOBOTTO.Members;
using THOBOTTO.Overview;

namespace THOBOTTO.Wrapped;

// The newest Wrapped the bot posted, for a few days after: its highlights, and its forum post to see it all.
public sealed class LatestWrappedWidget(IDbContextFactory<BotDbContext> dbFactory, WrappedService wrapped, HappeningForum forum, GatewayClient gateway, TimeZones zones, TimeProvider time) : IWidgetKind
{
    private const int Facts = 6;

    public const string KindId = "wrapped";

    public string Id => KindId;

    public string Name => "Latest Wrapped";

    public string Description => "the newest Wrapped's highlights, for a few days after it's out";

    public IReadOnlyList<WidgetOption> Options => [new("days", "Shown for", "7", "days after it's out, 1 to 31")];

    public TimeSpan Refresh => TimeSpan.FromMinutes(30);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var latest = await db.WrappedPosts.AsNoTracking().Where(p => p.GuildId == guildId).OrderByDescending(p => p.At).FirstOrDefaultAsync();
        var now = time.GetUtcNow();
        if (latest is null || now - latest.At > TimeSpan.FromDays(WidgetOptions.Int(options, "days", 7, 1, 31)) || !gateway.Cache.Guilds.TryGetValue(guildId, out var guild))
            return [];
        var (zone, _) = await zones.ForAsync(guildId, 0);
        var cards = await wrapped.ServerAsync(guild, PeriodOf(latest.Key, Instant.FromDateTimeOffset(now).InZone(zone).Date), id => $"<@{id}>");
        if (cards.FirstOrDefault(c => c.Facts.Count > 0) is not { } card)
            return [];
        var lines = card.Facts.Take(Facts).Select(f => $"**{f.Label}**: {f.Value}").ToList();
        if (await forum.LinkAsync(guildId, WrappedPoster.Happening.Id, WrappedPoster.ThingId(latest.Key)) is { } link)
            lines.Add($"[All of it, and the talk about it]({link})");
        return [new($"🎁 {WrappedPoster.Title(latest.Key)}", string.Join('\n', lines))];
    }

    // The period a posted Wrapped covered, as seen today: last month's in the month after; the year's on its last
    // day, and as last year's after.
    private static WrappedPeriod PeriodOf(string key, LocalDate today)
        => key.StartsWith("month:") ? WrappedPeriod.LastMonth
            : int.Parse(key[5..]) == today.Year ? WrappedPeriod.ThisYear : WrappedPeriod.LastYear;
}
