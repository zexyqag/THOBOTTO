using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using NetCord.Rest;

using THOBOTTO.Stats;

namespace THOBOTTO.Panel;

// Names for the ids in cases and the audit log: the member's server nickname where they're in the
// server, else their Discord name. Remembered a while, to spare Discord.
public sealed partial class PanelNames(RestClient rest, TimeProvider time)
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(ulong Guild, ulong User), (string Name, DateTimeOffset At)> _names = new();

    public async Task<IReadOnlyDictionary<ulong, string>> NamesAsync(ulong guildId, IEnumerable<ulong> userIds)
    {
        var names = new Dictionary<ulong, string>();
        foreach (var id in userIds.Distinct())
            names[id] = await NameAsync(guildId, id);
        return names;
    }

    // A member in a Wrapped built off Discord, filled in after with the others (WrappedAsync).
    public static string Mark(ulong id) => $"\u0001{id}\u0001";

    // The marked members as names; server emoji by name, as only Discord draws them.
    public async Task<WrappedCard> WrappedAsync(ulong guildId, WrappedCard card)
    {
        var all = card.Lists.SelectMany(l => l.Lines).Concat(card.Facts.Select(f => f.Value));
        var names = await NamesAsync(guildId, all.SelectMany(t => Marked().Matches(t)).Select(m => ulong.Parse(m.Groups[1].Value)));
        string Fill(string text) => CustomEmoji().Replace(Marked().Replace(text, m => names.GetValueOrDefault(ulong.Parse(m.Groups[1].Value)) ?? "someone"), ":$1:");
        return card with
        {
            Facts = card.Facts.Select(f => (f.Label, Fill(f.Value))).ToList(),
            Lists = card.Lists.Select(l => (l.Heading, (IReadOnlyList<string>)l.Lines.Select(Fill).ToList())).ToList(),
        };
    }

    [GeneratedRegex("\u0001(\\d+)\u0001")]
    private static partial Regex Marked();

    [GeneratedRegex(@"<a?:(\w+):\d+>")]
    private static partial Regex CustomEmoji();

    private async Task<string> NameAsync(ulong guildId, ulong userId)
    {
        var now = time.GetUtcNow();
        if (_names.TryGetValue((guildId, userId), out var known) && now - known.At < Remember)
            return known.Name;

        string name;
        try
        {
            var member = await rest.GetGuildUserAsync(guildId, userId);
            name = member.Nickname ?? member.GlobalName ?? member.Username;
        }
        catch (RestException)
        {
            try
            {
                var user = await rest.GetUserAsync(userId);
                name = user.GlobalName ?? user.Username;
            }
            catch (RestException)
            {
                name = userId.ToString();
            }
        }
        _names[(guildId, userId)] = (name, now);
        return name;
    }
}
