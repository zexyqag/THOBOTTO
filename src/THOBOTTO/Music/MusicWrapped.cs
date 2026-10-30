using THOBOTTO.Helpers;
using THOBOTTO.Wrapped;

using static THOBOTTO.Wrapped.WrappedText;

namespace THOBOTTO.Music;

// Music in Wrapped: the music card, the server's or a member's, and a Wrapped of its own for each voice that played.
public sealed class MusicWrapped(PlayStats stats, PersonalityBook personalities, HelperFleet fleet) : IWrappedSection, IWrappedVoices
{
    public WrappedCardKind Card => WrappedCardKind.Music;

    public int Order => 10;

    public string ModuleId => MusicService.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        var music = await stats.MusicAsync(ask.GuildId, new(UserId: ask.Member), ask.Span, ask.Zone, HelperName);
        if (music.Plays == 0)
            return null;
        if (ask.Member is { } me)
        {
            var asked = music.Requesters.FirstOrDefault(r => r.User == me).Count;
            return new($"heard {Count(music.Plays, "song")}, {Hours(music.Played)} of music",
                Facts(music, ("Songs you asked for", asked.ToString()), ("Different artists", music.Artists.ToString())),
                [
                    new("Your top songs", Plays(music.TopSongs)),
                    new("Your top artists", Plays(music.TopArtists)),
                    new("Your top genres", Genres(music.TopGenres)),
                    new("Listening buddies", [.. music.Companions.Select(c => WrappedLine.Of(c.User, $"{Count(c.Count, "song")} together"))], 3),
                    new("Your voices", Plays(music.Voices)),
                ]);
        }
        return new($"{Count(music.Plays, "song")} played, {Hours(music.Played)} of music",
            Facts(music, ("Different songs", music.Songs.ToString()), ("Artists", music.Artists.ToString()), ("Autoplay picked", Percent(music.AutoplayShare))),
            [
                new("Top songs", Plays(music.TopSongs)),
                new("Top artists", Plays(music.TopArtists)),
                new("Top genres", Genres(music.TopGenres)),
                new("Top DJs", [.. music.Requesters.Select(r => WrappedLine.Of(r.User, Count(r.Count, "request")))]),
                new("Most hours listening", [.. music.Listeners.Select(l => WrappedLine.Of(l.User, Hours(l.Heard)))]),
                new("Busiest voices", Plays(music.Voices)),
            ]);
    }

    public async Task<IReadOnlyList<(string Key, string Name)>> VoicesAsync(ulong guildId)
        => [.. (await stats.VoicesAsync(guildId)).Select(v => v.Personality is { } p ? ($"p:{p}", p) : ($"h:{v.HelperId}", HelperName(v.HelperId)))];

    public async Task<WrappedVoice?> VoiceAsync(WrappedAsk ask, string key)
    {
        PlayScope scope;
        string voice;
        Personality? personality = null;
        if (key.StartsWith("p:"))
        {
            voice = key[2..];
            scope = new(Personality: voice);
            personality = (await personalities.ListAsync(ask.GuildId)).FirstOrDefault(p => p.Name == voice);
        }
        else if (key.StartsWith("h:") && ulong.TryParse(key[2..], out var helperId))
            (voice, scope) = (HelperName(helperId), new(HelperId: helperId));
        else
            return null;

        var music = await stats.MusicAsync(ask.GuildId, scope, ask.Span, ask.Zone, HelperName);
        var lines = personality?.Phrases.GetValueOrDefault(Moments.Wrapped) is { Count: > 0 } own ? own : PersonalityFile.Plain.Lines[Moments.Wrapped];
        var intro = lines[Random.Shared.Next(lines.Count)].Replace("{helper}", voice).Replace("{period}", ask.Span.Label);
        var part = music.Plays == 0 ? null : new WrappedPart(null,
            Facts(music, ("Songs played", music.Plays.ToString()), ("Hours", Hours(music.Played))),
            [
                new("Top songs", Plays(music.TopSongs)),
                new("Top artists", Plays(music.TopArtists)),
                new("Top genres", Genres(music.TopGenres)),
                new("Best customers", [.. music.Requesters.Select(r => WrappedLine.Of(r.User, Count(r.Count, "request")))], 3),
            ]);
        return new(voice, personality?.Color ?? PersonalityFile.Plain.ColorValue, intro, part);
    }

    private string HelperName(ulong helperId) => fleet.Helpers.FirstOrDefault(h => h.UserId == helperId)?.Name ?? "a helper";

    private static List<(string, string)> Facts(MusicSummary music, params (string, string)[] first)
    {
        var facts = first.ToList();
        if (music.BusiestDay is { } day && music.BusiestHour is { } hour)
            facts.Add(("Busiest time", Busiest(day, hour)));
        facts.Add(("Skipped", Percent(music.SkipShare)));
        if (music.MostSkipped is { } skipped)
            facts.Add(("Most skipped", $"{skipped.Name} ({skipped.Count}×)"));
        return facts;
    }

    private static List<WrappedLine> Plays(IEnumerable<Ranked> ranked) => [.. ranked.Select(r => new WrappedLine(r.Name, Count(r.Count, "play")))];

    private static List<WrappedLine> Genres(IEnumerable<Ranked> ranked) => [.. ranked.Select(r => new WrappedLine(r.Name))];
}
