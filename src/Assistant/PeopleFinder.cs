using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Listening;
using THOBOTTO.Voice;

namespace THOBOTTO.Assistant;

// Where someone is, for telling people with the same name apart; better first.
public enum Whereabouts
{
    Offline,
    Online,
    JustMentioned,
    InTheCall,
}

public sealed record Person(ulong Id, string Name, Whereabouts Where, bool IsBot, double Score)
{
    public string Label => Where switch
    {
        Whereabouts.InTheCall => $"{Name} (in the call)",
        Whereabouts.JustMentioned => $"{Name} (just mentioned)",
        Whereabouts.Online => $"{Name} (online)",
        _ => $"{Name} (offline)",
    };
}

// Who a spoken name means: members whose names sound alike, the likeliest first.
public sealed class PeopleFinder(GatewayClient gateway, RestClient rest, VoicePresence presence)
{
    // How alike a spoken name and a member's must be: 1 is the same. Loose, as speech to text spells names
    // freely, and whatever is done to someone is asked about first.
    private const double Alike = 0.6;
    // Ahead by this much, a match needs no question.
    private const double ClearlyBetter = 0.15;
    private const int MostAsked = 3;

    // One person when it's clear who, several to ask between, or none.
    public async Task<IReadOnlyList<Person>> FindAsync(ulong guildId, ulong channelId, string spoken, ulong? mentioned)
    {
        if (!gateway.Cache.Guilds.TryGetValue(guildId, out var guild) || string.IsNullOrWhiteSpace(spoken))
            return [];
        var inCall = presence.Snapshot(guildId).Where(p => p.Value.ChannelId == channelId).Select(p => p.Key).ToHashSet();
        var members = guild.Users.Values.ToDictionary(u => u.Id);
        // Discord searches by how names start; a misheard name still shares its first letters.
        foreach (var found in await rest.FindGuildUserAsync(guildId, spoken.Trim()[..Math.Min(2, spoken.Trim().Length)], 100))
            members.TryAdd(found.Id, found);

        var people = members.Values
            .Select(m => (Member: m, Score: Score(spoken, Names(m))))
            .Where(m => m.Score >= Alike && !m.Member.IsBot)
            .Select(m => new Person(m.Member.Id, m.Member.Nickname ?? m.Member.GlobalName ?? m.Member.Username,
                inCall.Contains(m.Member.Id) ? Whereabouts.InTheCall
                    : m.Member.Id == mentioned ? Whereabouts.JustMentioned
                    : guild.Presences.TryGetValue(m.Member.Id, out var p) && p.Status != UserStatusType.Offline ? Whereabouts.Online
                    : Whereabouts.Offline,
                m.Member.IsBot, m.Score))
            .ToList();
        return Pick(people);
    }

    // The best when it stands out (closer, or more at hand), else those as good as it.
    public static IReadOnlyList<Person> Pick(IReadOnlyList<Person> people)
    {
        var ranked = people.OrderByDescending(p => p.Where).ThenByDescending(p => p.Score).ToList();
        if (ranked.Count <= 1)
            return ranked;
        var (best, next) = (ranked[0], ranked[1]);
        if (best.Where > next.Where || best.Score - next.Score >= ClearlyBetter)
            return [best];
        return ranked.TakeWhile(p => p.Where == best.Where && best.Score - p.Score < ClearlyBetter).Take(MostAsked).ToList();
    }

    private static IEnumerable<string> Names(GuildUser member)
        => new[] { member.Nickname, member.GlobalName, member.Username }.OfType<string>();

    // The closest of their names, whole or by word ("Ana" matches "Ana Smith").
    public static double Score(string spoken, IEnumerable<string> names)
    {
        var said = Plain(spoken);
        return names.SelectMany(n => Words(n).Prepend(Plain(n)))
            .Select(n => VoiceCommandParser.Similarity(said, n))
            .DefaultIfEmpty(0).Max();
    }

    // Letters only: a misheard name picks up digits ("mardi5").
    private static string Plain(string text) => string.Concat(text.ToLowerInvariant().Where(char.IsLetter));

    private static IEnumerable<string> Words(string text)
        => text.ToLowerInvariant().Split([' ', '_', '.', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Plain).Where(w => w.Length > 0);
}
