using Microsoft.EntityFrameworkCore;

using NetCord.Services.ApplicationCommands;

using NodaTime;

using THOBOTTO.Data;

namespace THOBOTTO.Stats;

public enum WrappedPeriod
{
    [SlashCommandChoice(Name = "This month")] ThisMonth,
    [SlashCommandChoice(Name = "Last month")] LastMonth,
    [SlashCommandChoice(Name = "This year")] ThisYear,
    [SlashCommandChoice(Name = "Last year")] LastYear,
    [SlashCommandChoice(Name = "All time")] AllTime,
}

// A stretch of time in a zone, with how to say it ("October 2026", "2026", "all time").
public sealed record WrappedSpan(string Label, DateTimeOffset From, DateTimeOffset To)
{
    public static WrappedSpan For(WrappedPeriod period, DateTimeZone zone, Instant now)
    {
        var today = now.InZone(zone).Date;
        var month = new LocalDate(today.Year, today.Month, 1);
        var year = new LocalDate(today.Year, 1, 1);
        return period switch
        {
            WrappedPeriod.ThisMonth => Make(month.ToString("MMMM yyyy", null), month, month.PlusMonths(1)),
            WrappedPeriod.LastMonth => Make(month.PlusMonths(-1).ToString("MMMM yyyy", null), month.PlusMonths(-1), month),
            WrappedPeriod.ThisYear => Make(today.Year.ToString(), year, year.PlusYears(1)),
            WrappedPeriod.LastYear => Make((today.Year - 1).ToString(), year.PlusYears(-1), year),
            _ => new("all time", DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
        };

        WrappedSpan Make(string label, LocalDate from, LocalDate to)
            => new(label, from.AtStartOfDayInZone(zone).ToInstant().ToDateTimeOffset(), to.AtStartOfDayInZone(zone).ToInstant().ToDateTimeOffset());
    }
}

// Whose music: the whole server, a voice (a personality, or a helper wearing none), or a member as a listener.
public sealed record WrappedScope(ulong? UserId = null, string? Personality = null, ulong? HelperId = null);

public sealed record Ranked(string Name, int Count);

public sealed record MusicSummary(
    int Plays,
    TimeSpan Played,
    int Songs,
    int Artists,
    IReadOnlyList<Ranked> TopSongs,
    IReadOnlyList<Ranked> TopArtists,
    IReadOnlyList<Ranked> TopGenres,
    Ranked? MostSkipped,
    double SkipShare,
    double AutoplayShare,
    int? BusiestHour,
    IsoDayOfWeek? BusiestDay,
    IReadOnlyList<(ulong User, int Count)> Requesters,
    IReadOnlyList<(ulong User, TimeSpan Heard)> Listeners,
    IReadOnlyList<Ranked> Voices,
    IReadOnlyList<(ulong User, int Count)> Companions);

// The numbers behind a music Wrapped, from the play history.
public sealed class WrappedStats(IDbContextFactory<BotDbContext> dbFactory, GenreBook genres)
{
    private const int Top = 5;
    // A play counts once heard this long, or played to its end.
    private const long Counted = 30_000;

    public async Task<MusicSummary> MusicAsync(ulong guildId, WrappedScope scope, WrappedSpan span, DateTimeZone zone, Func<ulong, string> helperName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.PlayRecords.AsNoTracking().Where(p => p.GuildId == guildId && p.StartedAt >= span.From && p.StartedAt < span.To);
        if (scope.Personality is { } personality)
            query = query.Where(p => p.Personality == personality);
        else if (scope.HelperId is { } helperId)
            query = query.Where(p => p.HelperId == helperId && p.Personality == null);
        if (scope.UserId is { } userId)
            query = query.Where(p => db.PlayListeners.Any(l => l.PlayId == p.Id && l.UserId == userId));

        var all = await query.ToListAsync();
        var ids = all.Select(p => p.Id).ToList();
        var heard = await db.PlayListeners.AsNoTracking().Where(l => ids.Contains(l.PlayId)).ToListAsync();
        var plays = all.Where(p => p.PlayedMs >= Counted || !p.Skipped).ToList();

        static string Song(PlayRecord p) => $"{p.Title} · {p.Artist}";
        var topArtists = Rank(plays.Select(p => p.Artist), Top);
        var genreOf = await genres.ForAsync(Rank(plays.Select(p => p.Artist), 30).Select(r => r.Name));
        var genreScores = new Dictionary<string, double>();
        foreach (var play in plays)
        {
            if (!genreOf.TryGetValue(play.Artist.ToLowerInvariant(), out var found))
                continue;
            // The best-fitting genre counts most.
            for (var i = 0; i < found.Count; i++)
                genreScores[found[i]] = genreScores.GetValueOrDefault(found[i]) + 1.0 / (1 << i);
        }

        var local = plays.Select(p => Instant.FromDateTimeOffset(p.StartedAt).InZone(zone)).ToList();
        var playIds = plays.Select(p => p.Id).ToHashSet();
        var counted = heard.Where(l => playIds.Contains(l.PlayId)).ToList();
        var byId = plays.ToDictionary(p => p.Id);

        return new(
            plays.Count,
            TimeSpan.FromMilliseconds(plays.Sum(p => p.PlayedMs)),
            plays.Select(Song).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            plays.Select(p => p.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Rank(plays.Select(Song), Top),
            topArtists,
            genreScores.OrderByDescending(g => g.Value).Take(Top).Select(g => new Ranked(g.Key, (int)Math.Round(g.Value))).ToList(),
            Rank(all.Where(p => p.Skipped).Select(Song), 1).FirstOrDefault(r => r.Count >= 2),
            all.Count == 0 ? 0 : (double)all.Count(p => p.Skipped) / all.Count,
            plays.Count == 0 ? 0 : (double)plays.Count(p => p.Autoplay) / plays.Count,
            local.Count == 0 ? null : local.GroupBy(t => t.Hour).MaxBy(g => g.Count())!.Key,
            local.Count == 0 ? null : local.GroupBy(t => t.DayOfWeek).MaxBy(g => g.Count())!.Key,
            plays.Where(p => !p.Autoplay).GroupBy(p => p.RequestedBy).Select(g => (g.Key, g.Count())).OrderByDescending(r => r.Item2).ToList(),
            counted.GroupBy(l => l.UserId).Select(g => (g.Key, TimeSpan.FromMilliseconds(g.Sum(l => byId[l.PlayId].PlayedMs)))).OrderByDescending(r => r.Item2).ToList(),
            Rank(plays.Select(p => p.Personality ?? helperName(p.HelperId)), Top),
            scope.UserId is { } me
                ? counted.Where(l => l.UserId != me).GroupBy(l => l.UserId).Select(g => (g.Key, g.Count())).OrderByDescending(r => r.Item2).ToList()
                : []);
    }

    // The voices (personalities, and helpers wearing none) that played in a server, most plays first.
    public async Task<IReadOnlyList<(string? Personality, ulong HelperId, int Plays)>> VoicesAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var voices = await db.PlayRecords.AsNoTracking().Where(p => p.GuildId == guildId)
            .GroupBy(p => new { p.Personality, Helper = p.Personality == null ? p.HelperId : 0 })
            .Select(g => new { g.Key.Personality, g.Key.Helper, Plays = g.Count() })
            .OrderByDescending(v => v.Plays)
            .ToListAsync();
        return voices.Select(v => (v.Personality, v.Helper, v.Plays)).ToList();
    }

    private static List<Ranked> Rank(IEnumerable<string> names, int take)
        => names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Select(g => new Ranked(g.First(), g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).Take(take).ToList();
}
