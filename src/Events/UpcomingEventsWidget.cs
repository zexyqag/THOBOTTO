using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Overview;

namespace THOBOTTO.Events;

// The next events, with who's coming.
public sealed class UpcomingEventsWidget(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time) : IWidgetKind
{
    public string Id => "events";

    public string Name => "Upcoming events";

    public string Description => "the next events and sessions, with how many are coming";

    public IReadOnlyList<WidgetOption> Options => [new("count", "How many", "5", "1 to 15")];

    public TimeSpan Refresh => TimeSpan.FromMinutes(2);

    public async Task<WidgetView?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var events = await db.Events.AsNoTracking()
            .Where(e => e.GuildId == guildId && e.State == EventStates.Scheduled && (e.StartsAt == null || e.StartsAt > now))
            .OrderBy(e => e.StartsAt ?? e.PollClosesAt).Take(WidgetOptions.Int(options, "count", 5, 1, 15)).ToListAsync();
        if (events.Count == 0)
            return null;
        var ids = events.Select(e => e.Id).ToList();
        var coming = await db.EventRsvps.Where(r => ids.Contains(r.EventId) && r.Status == RsvpStatuses.In).GroupBy(r => r.EventId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var lines = events.Select(e => e.StartsAt is { } at
            ? $"**{e.Title}** · <t:{at.ToUnixTimeSeconds()}:f> (<t:{at.ToUnixTimeSeconds()}:R>) · {coming.GetValueOrDefault(e.Id)} coming{(e.Capacity is { } cap ? $" of {cap}" : "")}"
            : $"**{e.Title}** · voting on when{(e.PollClosesAt is { } closes ? $", closes <t:{closes.ToUnixTimeSeconds()}:R>" : "")}");
        return new(null, new() { Title = "Coming up", Description = string.Join('\n', lines), Color = new(0x5865F2) });
    }
}
