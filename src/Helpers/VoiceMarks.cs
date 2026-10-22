using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace THOBOTTO.Helpers;

// A piece of a line as it's to be said: its text, how fast and how high next to the voice's own, whether it
// glitches, and a silence before it.
public sealed record Piece(string Text, double Speed = 1, int Pitch = 0, bool Glitch = false, TimeSpan Pause = default);

// The bot's own marks in a line, in square brackets: [pause], [pause 1s], and the spans [slow]…[/slow],
// [fast]…[/fast], [high]…[/high], [low]…[/low], [spell]…[/spell] (said letter by letter) and [glitch]…[/glitch].
// Every engine gets them rendered the bot's way, so a personality file carries no engine's syntax; the chat copy
// of a line drops them. Anything else in brackets is text.
public static partial class VoiceMarks
{
    private static readonly IReadOnlySet<string> Spans = new HashSet<string> { "slow", "fast", "high", "low", "spell", "glitch" };
    private static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<Piece> Parse(string text)
    {
        var parts = new List<Piece>();
        var open = new List<string>();
        var buffer = new StringBuilder();
        var pause = TimeSpan.Zero;
        var at = 0;
        foreach (Match m in Mark().Matches(text))
        {
            buffer.Append(text, at, m.Index - at);
            at = m.Index + m.Length;
            var name = m.Groups["name"].Value.ToLowerInvariant();
            var closing = m.Groups["close"].Success;
            if (name == "pause" && !closing)
            {
                Flush(parts, buffer, open, ref pause);
                pause += PauseOf(m.Groups["arg"].Value);
            }
            else if (Spans.Contains(name) && (closing ? open.Contains(name) : true))
            {
                Flush(parts, buffer, open, ref pause);
                if (closing)
                    open.RemoveAt(open.LastIndexOf(name));
                else
                    open.Add(name);
            }
            else
                buffer.Append(m.Value);
        }
        buffer.Append(text, at, text.Length - at);
        Flush(parts, buffer, open, ref pause, last: true);
        return parts;
    }

    // Whitespace between marks is no piece of its own: a pause there waits for the next words (or ends the line).
    private static void Flush(List<Piece> parts, StringBuilder buffer, List<string> open, ref TimeSpan pause, bool last = false)
    {
        var text = buffer.ToString();
        if (text.Trim().Length == 0 && (pause == TimeSpan.Zero || !last))
        {
            buffer.Clear();
            return;
        }
        if (open.Contains("spell"))
            text = Spelled(text);
        parts.Add(new(text, open.Contains("fast") ? 1.35 : open.Contains("slow") ? 0.75 : 1, open.Contains("high") ? 5 : open.Contains("low") ? -5 : 0, open.Contains("glitch"), pause));
        buffer.Clear();
        pause = TimeSpan.Zero;
    }

    // The line for chat: the marks gone, the words kept.
    public static string Strip(string text)
    {
        var plain = Mark().Replace(text, m => IsMark(m) ? "" : m.Value);
        return Spaces().Replace(plain, " ").Replace(" ,", ",").Replace(" .", ".").Replace(" !", "!").Replace(" ?", "?").Trim();
    }

    // Why the marks in a line don't work, or null.
    public static string? Problem(string text)
    {
        var open = new List<string>();
        foreach (Match m in Mark().Matches(text))
        {
            var name = m.Groups["name"].Value.ToLowerInvariant();
            if (!Spans.Contains(name))
                continue;
            if (!m.Groups["close"].Success)
                open.Add(name);
            else if (open.Contains(name))
                open.RemoveAt(open.LastIndexOf(name));
            else
                return $"[/{name}] without a [{name}] before it";
        }
        return open.Count == 0 ? null : $"[{open[^1]}] isn't closed with [/{open[^1]}]";
    }

    public static bool IsMark(Match m)
    {
        var name = m.Groups["name"].Value.ToLowerInvariant();
        return name == "pause" ? !m.Groups["close"].Success : Spans.Contains(name);
    }

    // "404" as "4 0 4", "Unit" as "U N I T": letters an engine reads one by one.
    public static string Spelled(string text)
    {
        var spelled = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                spelled.Append(char.ToUpperInvariant(c)).Append(' ');
            else if (c == ' ')
                spelled.Append(", ");
            else
                spelled.Append(c);
        }
        return spelled.ToString().Trim();
    }

    private static TimeSpan PauseOf(string arg)
    {
        var pause = arg.Trim().ToLowerInvariant() switch
        {
            "" => DefaultPause,
            var ms when ms.EndsWith("ms") && double.TryParse(ms[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => TimeSpan.FromMilliseconds(v),
            var s when s.EndsWith('s') && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => TimeSpan.FromSeconds(v),
            var n when double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => TimeSpan.FromSeconds(v),
            _ => DefaultPause,
        };
        return pause < TimeSpan.Zero ? TimeSpan.Zero : pause > LongestPause ? LongestPause : pause;
    }

    [GeneratedRegex(@"\[(?<close>/)?(?<name>[A-Za-z]+)(?:\s+(?<arg>[^\]]*))?\]")]
    private static partial Regex Mark();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();
}
