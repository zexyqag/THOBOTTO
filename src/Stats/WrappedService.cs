using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Stats;

// A Wrapped to show: in Discord as an embed, in the panel as a card.
public sealed record WrappedCard(
    string Title,
    string? Intro,
    int? Color,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<(string Heading, IReadOnlyList<string> Lines)> Lists);

// Builds Wrapped for a server, a voice (personality or plain helper) or a member. Only members who opted in
// are named in rankings; everyone counts in the totals. A member always sees themselves in their own.
public sealed class WrappedService(
    WrappedStats stats,
    ActivityStats activity,
    SettingsStore settings,
    GatewayClient gateway,
    PersonalityBook personalities,
    HelperFleet fleet,
    TimeZones zones,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time)
{
    private const int Named = 5;

    public async Task<bool> IsInAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WrappedOptIns.AnyAsync(o => o.GuildId == guildId && o.UserId == userId);
    }

    public async Task SetInAsync(ulong guildId, ulong userId, bool include)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!include)
            await db.WrappedOptIns.Where(o => o.GuildId == guildId && o.UserId == userId).ExecuteDeleteAsync();
        else if (!await db.WrappedOptIns.AnyAsync(o => o.GuildId == guildId && o.UserId == userId))
        {
            db.WrappedOptIns.Add(new() { GuildId = guildId, UserId = userId, At = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
    }

    // Voices to pick from: "p:<personality>" or "h:<helper id>", with their names.
    public async Task<IReadOnlyList<(string Key, string Name)>> VoicesAsync(ulong guildId)
        => (await stats.VoicesAsync(guildId))
            .Select(v => v.Personality is { } p ? ($"p:{p}", p) : ($"h:{v.HelperId}", HelperName(v.HelperId)))
            .ToList();

    // The music card, and the Discord card when there was any activity.
    public async Task<IReadOnlyList<WrappedCard>> ServerAsync(Guild guild, WrappedPeriod period, Func<ulong, string> name)
    {
        var (span, zone) = await SpanAsync(guild.Id, 0, period);
        var music = await ServerMusicAsync(guild, span, zone, name);
        var discord = await activity.ForAsync(guild.Id, null, span, zone, Bots());
        if (discord.Empty)
            return [music];
        // Without music, the Discord card stands alone.
        IReadOnlyList<WrappedCard> before = music.Facts.Count == 0 ? [] : [music];
        var named = await NamedAsync(guild.Id);
        var points = await settings.GetAsync<PointRules>(guild.Id, PointsEngine.ModuleId);
        IEnumerable<(string, string)> Top<T>(IEnumerable<(ulong User, T Value)> ranked, Func<T, string> show)
            => ranked.Where(r => named.Contains(r.User)).Take(Named).Select(r => (name(r.User), show(r.Value)));
        return [.. before, new(
            $"💬 {guild.Name} on Discord · {span.Label}",
            $"{Count(discord.Messages, "message")}, {Hours(discord.Voice)} in voice.",
            null,
            ActivityFacts(discord, points,
                ("Events held", discord.Events.ToString()),
                ("Kudos given", discord.Kudos.ToString()),
                ("Quotes saved", discord.Quotes.ToString()),
                ("Hall of fame", discord.Fame.ToString())),
            Lists(
                ("Busiest channels", Numbered(discord.Channels.Take(Named).Select(c => (Channel(guild, c.Channel), Count(c.Count, "message"))))),
                ("Top emoji", discord.Emoji.Select((e, i) => $"{i + 1}. {e.Emoji} · {e.Count}×").ToList()),
                ("Chattiest", Numbered(Top(discord.Talkers, c => Count(c, "message")))),
                ("Most time in voice", Numbered(Top(discord.InVoice, Hours))),
                ("Most kudos", Numbered(Top(discord.KudosReceived, c => Count(c, "kudos")))),
                ("Most quoted", Numbered(Top(discord.Quoted, c => Count(c, "quote")))),
                ("Hall of famers", Numbered(Top(discord.Famous, c => Count(c, "entry")))),
                ("Event regulars", Numbered(Top(discord.EventGoers, c => Count(c, "event")))),
                ("Top earners", Numbered(Top(discord.Earners, points.Format)))))];
    }

    private async Task<WrappedCard> ServerMusicAsync(Guild guild, WrappedSpan span, DateTimeZone zone, Func<ulong, string> name)
    {
        var music = await stats.MusicAsync(guild.Id, new(), span, zone, HelperName);
        var named = await NamedAsync(guild.Id);
        if (music.Plays == 0)
            return Empty($"🎧 {guild.Name} Wrapped · {span.Label}", span);

        return new(
            $"🎧 {guild.Name} Wrapped · {span.Label}",
            $"{Count(music.Plays, "song")} played, {Hours(music.Played)} of music.",
            null,
            Facts(music, ("Different songs", music.Songs.ToString()), ("Artists", music.Artists.ToString()), ("Autoplay picked", Percent(music.AutoplayShare))),
            Lists(
                ("Top songs", Numbered(music.TopSongs)),
                ("Top artists", Numbered(music.TopArtists)),
                ("Top genres", music.TopGenres.Select((g, i) => $"{i + 1}. {g.Name}").ToList()),
                ("Top DJs", Numbered(music.Requesters.Where(r => named.Contains(r.User)).Take(Named).Select(r => (name(r.User), $"{Count(r.Count, "request")}")))),
                ("Most hours listening", Numbered(music.Listeners.Where(l => named.Contains(l.User)).Take(Named).Select(l => (name(l.User), Hours(l.Heard))))),
                ("Busiest voices", Numbered(music.Voices))));
    }

    public async Task<WrappedCard?> VoiceAsync(Guild guild, string key, WrappedPeriod period, Func<ulong, string> name)
    {
        WrappedScope scope;
        string voice;
        Personality? personality = null;
        if (key.StartsWith("p:"))
        {
            voice = key[2..];
            scope = new(Personality: voice);
            personality = (await personalities.ListAsync(guild.Id)).FirstOrDefault(p => p.Name == voice);
        }
        else if (key.StartsWith("h:") && ulong.TryParse(key[2..], out var helperId))
            (voice, scope) = (HelperName(helperId), new(HelperId: helperId));
        else
            return null;

        var (span, zone) = await SpanAsync(guild.Id, 0, period);
        var music = await stats.MusicAsync(guild.Id, scope, span, zone, HelperName);
        var named = await NamedAsync(guild.Id);
        var color = personality?.Color ?? PersonalityFile.Plain.ColorValue;
        if (music.Plays == 0)
            return Empty($"🎙️ {voice}'s Wrapped · {span.Label}", span) with { Color = color };

        // A personality made before it had Wrapped lines borrows its template's, when it was made from one.
        var lines = personality?.Phrases.GetValueOrDefault(Moments.Wrapped) is { Count: > 0 } own ? own
            : PersonalityFile.Templates.FirstOrDefault(t => t.Name == voice)?.Lines.GetValueOrDefault(Moments.Wrapped) ?? PersonalityFile.Plain.Lines[Moments.Wrapped];
        var intro = lines[Random.Shared.Next(lines.Count)].Replace("{helper}", voice).Replace("{period}", span.Label);
        return new(
            $"🎙️ {voice}'s Wrapped · {span.Label}",
            intro,
            color,
            Facts(music, ("Songs played", music.Plays.ToString()), ("Hours", Hours(music.Played))),
            Lists(
                ("Top songs", Numbered(music.TopSongs)),
                ("Top artists", Numbered(music.TopArtists)),
                ("Top genres", music.TopGenres.Select((g, i) => $"{i + 1}. {g.Name}").ToList()),
                ("Best customers", Numbered(music.Requesters.Where(r => named.Contains(r.User)).Take(3).Select(r => (name(r.User), $"{Count(r.Count, "request")}"))))));
    }

    // Owner: how the title names them ("Your", "Ana's"). The music card, and the Discord card when there was any activity.
    public async Task<IReadOnlyList<WrappedCard>> MemberAsync(Guild guild, ulong userId, string owner, WrappedPeriod period, Func<ulong, string> name)
    {
        var (span, zone) = await SpanAsync(guild.Id, userId, period);
        var music = await MemberMusicAsync(guild, userId, owner, span, zone, name);
        var mine = await activity.ForAsync(guild.Id, userId, span, zone, Bots());
        if (mine.Empty)
            return [music];
        IReadOnlyList<WrappedCard> before = music.Facts.Count == 0 ? [] : [music];

        // Where they stand among everyone, without naming anyone else.
        var everyone = await activity.ForAsync(guild.Id, null, span, zone, Bots());
        static string Rank<T>(IReadOnlyList<(ulong User, T)> ranked, ulong user)
            => ranked.Select((r, i) => (r.User, i)).FirstOrDefault(r => r.User == user) is { User: > 0 } found ? $"#{found.i + 1} of {ranked.Count}" : "";
        var points = await settings.GetAsync<PointRules>(guild.Id, PointsEngine.ModuleId);
        var kudosIn = everyone.KudosReceived.FirstOrDefault(k => k.User == userId).Count;
        var kudosOut = everyone.KudosGiven.FirstOrDefault(k => k.User == userId).Count;
        return [.. before, new(
            $"💬 {owner} Discord · {span.Label}",
            $"You sent {Count(mine.Messages, "message")} and spent {Hours(mine.Voice)} in voice.",
            null,
            ActivityFacts(mine, points,
                ("Chattiest rank", Rank(everyone.Talkers, userId)),
                ("Voice rank", Rank(everyone.InVoice, userId)),
                ("Events joined", mine.Events.ToString()),
                ("Kudos", $"{kudosIn} received, {kudosOut} given"),
                ("Quoted", Count(mine.Quotes, "time")),
                ("Hall of fame", Count(mine.Fame, "entry"))),
            Lists(
                ("Your channels", Numbered(mine.Channels.Take(Named).Select(c => (Channel(guild, c.Channel), Count(c.Count, "message"))))),
                ("Your emoji", mine.Emoji.Select((e, i) => $"{i + 1}. {e.Emoji} · {e.Count}×").ToList()),
                ("Your voice channels", Numbered(mine.VoiceChannels.Select(c => (Channel(guild, c.Channel), Hours(c.Time)))))))];
    }

    private async Task<WrappedCard> MemberMusicAsync(Guild guild, ulong userId, string owner, WrappedSpan span, DateTimeZone zone, Func<ulong, string> name)
    {
        var music = await stats.MusicAsync(guild.Id, new(UserId: userId), span, zone, HelperName);
        var named = await NamedAsync(guild.Id);
        if (music.Plays == 0)
            return Empty($"✨ {owner} Wrapped · {span.Label}", span);

        var asked = music.Requesters.FirstOrDefault(r => r.User == userId).Count;
        return new(
            $"✨ {owner} Wrapped · {span.Label}",
            $"You heard {Count(music.Plays, "song")}, {Hours(music.Played)} of music.",
            null,
            Facts(music, ("Songs you asked for", asked.ToString()), ("Different artists", music.Artists.ToString())),
            Lists(
                ("Your top songs", Numbered(music.TopSongs)),
                ("Your top artists", Numbered(music.TopArtists)),
                ("Your top genres", music.TopGenres.Select((g, i) => $"{i + 1}. {g.Name}").ToList()),
                ("Listening buddies", Numbered(music.Companions.Where(c => named.Contains(c.User)).Take(3).Select(c => (name(c.User), $"{Count(c.Count, "song")} together")))),
                ("Your voices", Numbered(music.Voices))));
    }

    private async Task<(WrappedSpan Span, DateTimeZone Zone)> SpanAsync(ulong guildId, ulong userId, WrappedPeriod period)
    {
        var (zone, _) = await zones.ForAsync(guildId, userId);
        return (WrappedSpan.For(period, zone, Instant.FromDateTimeOffset(time.GetUtcNow())), zone);
    }

    private async Task<HashSet<ulong>> NamedAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.WrappedOptIns.Where(o => o.GuildId == guildId).Select(o => o.UserId).ToListAsync()).ToHashSet();
    }

    // Messages from the bot itself and its helpers aren't anyone's activity.
    private HashSet<ulong> Bots() => [.. fleet.Helpers.Select(h => h.UserId), .. gateway.Cache.User is { } bot ? [bot.Id] : Array.Empty<ulong>()];

    private static string Channel(Guild guild, ulong id) => guild.Channels.TryGetValue(id, out var channel) ? $"#{channel.Name}" : "a deleted channel";

    // Facts with nothing to say (empty, zero) are left out.
    private static List<(string, string)> ActivityFacts(ActivitySummary activity, PointRules points, params (string Label, string Value)[] facts)
    {
        var list = new List<(string, string)> { ("Messages", activity.Messages.ToString()), ("Hours in voice", Hours(activity.Voice)) };
        if (activity.BusiestDay is { } day && activity.BusiestHour is { } hour)
            list.Add(("Busiest time", $"{day}s around {hour:00}:00"));
        list.AddRange(facts.Where(f => f.Value is not ("" or "0" or "0 times" or "0 entries" or "0 received, 0 given")));
        if (activity.Earned > 0)
            list.Add(("Earned", points.Format(activity.Earned)));
        return list;
    }

    private string HelperName(ulong helperId) => fleet.Helpers.FirstOrDefault(h => h.UserId == helperId)?.Name ?? "a helper";

    private static WrappedCard Empty(string title, WrappedSpan span) => new(title, $"No music yet for {span.Label}.", null, [], []);

    private static List<(string, string)> Facts(MusicSummary music, params (string, string)[] first)
    {
        var facts = first.ToList();
        if (music.BusiestDay is { } day && music.BusiestHour is { } hour)
            facts.Add(("Busiest time", $"{day}s around {hour:00}:00"));
        facts.Add(("Skipped", Percent(music.SkipShare)));
        if (music.MostSkipped is { } skipped)
            facts.Add(("Most skipped", $"{skipped.Name} ({skipped.Count}×)"));
        return facts;
    }

    // Lists with nothing in them are left out.
    private static List<(string, IReadOnlyList<string>)> Lists(params (string Heading, IReadOnlyList<string> Lines)[] lists)
        => lists.Where(l => l.Lines.Count > 0).Select(l => (l.Heading, l.Lines)).ToList();

    private static IReadOnlyList<string> Numbered(IEnumerable<Ranked> ranked) => Numbered(ranked.Select(r => (r.Name, Count(r.Count, "play"))));

    private static IReadOnlyList<string> Numbered(IEnumerable<(string Name, string Detail)> items) => items.Select((x, i) => $"{i + 1}. {x.Name} · {x.Detail}").ToList();

    private static string Count(int n, string what) => $"{n} {(n == 1 ? what : what.EndsWith('y') ? what[..^1] + "ies" : what + "s")}";

    private static string Percent(double share) => $"{share * 100:0}%";

    public static string Hours(TimeSpan span) => span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours" : $"{span.TotalMinutes:0} minutes";
}
