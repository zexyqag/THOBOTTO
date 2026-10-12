using System.Text.RegularExpressions;

using THOBOTTO.Panel;

namespace THOBOTTO.Stats;

// The members in a Wrapped card, by name.
public sealed partial class WrappedNames(PanelNames names)
{
    // A member in a Wrapped built off Discord, filled in after with the others (FillAsync).
    public static string Mark(ulong id) => $"\u0001{id}\u0001";

    // The marked members as names; server emoji by name, as only Discord draws them.
    public async Task<WrappedCard> FillAsync(ulong guildId, WrappedCard card)
    {
        var all = card.Lists.SelectMany(l => l.Lines).Concat(card.Facts.Select(f => f.Value));
        var people = await names.NamesAsync(guildId, all.SelectMany(t => Marked().Matches(t)).Select(m => ulong.Parse(m.Groups[1].Value)));
        string Fill(string text) => CustomEmoji().Replace(Marked().Replace(text, m => people.GetValueOrDefault(ulong.Parse(m.Groups[1].Value)) ?? "someone"), ":$1:");
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
}
