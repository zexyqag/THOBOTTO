using System.Text.RegularExpressions;

namespace THOBOTTO.Listening;

public enum VoiceIntent
{
    // Addressed, but not understood.
    Unknown,
    Play,
    Queue,
    QueueFirst,
    Quote,
    Sound,
    Skip,
    Pause,
    Resume,
    Stop,
    Louder,
    Quieter,
    Volume,
    NowPlaying,
    Shuffle,
    LoopTrack,
    LoopQueue,
    LoopOff,
}

public sealed record VoiceCommand(string Name, VoiceIntent Intent, string? Argument, string Said);

// Reads a heard sentence: is it addressed to one of the names (said first, maybe after "hey"; heard loosely,
// as speech to text spells names freely), and what does it ask for.
public static partial class VoiceCommandParser
{
    // How alike a heard word must be to a name: 1 is the same.
    private const double Alike = 0.6;
    private const double AlikeShort = 0.75;

    public static VoiceCommand? Parse(string heard, IReadOnlyList<string> names)
    {
        var words = Words(heard);
        if (words.Count > 0 && words[0] is "hey" or "hi" or "okay" or "ok" or "yo")
            words.RemoveAt(0);

        // The best-matching name, by the words it takes up at the start.
        (string Name, int Words, double Score) best = default;
        foreach (var name in names)
        {
            var nameWords = Words(name);
            if (nameWords.Count == 0)
                continue;
            for (var take = Math.Max(1, nameWords.Count - 1); take <= Math.Min(words.Count, nameWords.Count + 1); take++)
            {
                var score = Similarity(string.Concat(words.Take(take)), string.Concat(nameWords));
                if (score > best.Score)
                    best = (name, take, score);
            }
        }
        var needed = best.Name is not null && string.Concat(Words(best.Name)).Length <= 4 ? AlikeShort : Alike;
        if (best.Name is null || best.Score < needed)
        {
            // "Yeast, play …": a misheard name still counts when a command follows at once and it sounds alike.
            if (words.Count < 2 || !CommandWords.Contains(words[1]) || names.Select(n => (Name: n, Score: SoundsAlike(words[0], string.Concat(Words(n))))).MaxBy(n => n.Score) is not { Score: > 0 } alike)
                return null;
            best = (alike.Name, 1, alike.Score);
        }

        var rest = string.Join(' ', words.Skip(best.Words));
        rest = Filler().Replace(rest, "").Trim();
        var (intent, argument) = Intent(rest);
        return new(best.Name, intent, argument, rest);
    }

    private static (VoiceIntent, string?) Intent(string said)
    {
        if (SoundNamed().Match(said) is { Success: true } sound)
            return (VoiceIntent.Sound, sound.Groups["what"].Value.Trim());
        if (QuoteThat().Match(said) is { Success: true } quote)
            return (VoiceIntent.Quote, quote.Groups["who"].Value.Trim());
        // "play X next" before "play X"; "queue X" and "add X" go last.
        if (QueueFirst().Match(said) is { Success: true } first)
            return (VoiceIntent.QueueFirst, first.Groups["what"].Value.Trim());
        if (Queue().Match(said) is { Success: true } queue)
            return (VoiceIntent.Queue, queue.Groups["what"].Value.Trim());
        if (Play().Match(said) is { Success: true } play)
            return (VoiceIntent.Play, play.Groups["what"].Value.Trim());
        if (VolumeTo().Match(said) is { Success: true } volume)
            return (VoiceIntent.Volume, volume.Groups["n"].Value);
        // The phrase said, else the nearest one (speech to text gets words slightly wrong), as long as no
        // phrase meaning something else is nearly as near ("turn it out": up or down?).
        if (Phrases.FirstOrDefault(p => p.Phrase == said) is { Phrase: not null } exact)
            return (exact.Intent, null);
        var ranked = Phrases.Select(p => (p.Intent, Score: Similarity(p.Phrase.Replace(" ", ""), said.Replace(" ", "")))).OrderByDescending(p => p.Score).ToList();
        var best = ranked[0];
        var rival = ranked.FirstOrDefault(p => p.Intent != best.Intent).Score;
        return best.Score >= AlikePhrase && best.Score - rival >= Margin ? (best.Intent, null) : (VoiceIntent.Unknown, null);
    }

    private static readonly HashSet<string> CommandWords = ["play", "playing", "queue", "add", "skip", "pause", "resume", "stop", "turn", "louder", "quieter", "volume", "shuffle", "loop", "quote", "whats"];

    // How alike two words sound (0 when not alike enough): by their consonants, in sound classes (j, g and y
    // alike; v, f, b and p; s, z and c), the first sound the same, or very alike throughout.
    public static double SoundsAlike(string heard, string name)
    {
        var (a, b) = (Sounds(heard), Sounds(name));
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var score = Similarity(a, b);
        return score >= 0.8 || a[0] == b[0] && score >= 0.6 ? score : 0;
    }

    private static string Sounds(string word)
    {
        var sounds = new System.Text.StringBuilder();
        foreach (var c in word.ToLowerInvariant().Replace("ph", "f"))
        {
            var sound = c switch
            {
                'j' or 'g' or 'y' => 'j',
                'v' or 'f' or 'b' or 'p' => 'f',
                's' or 'z' or 'c' => 's',
                'a' or 'e' or 'i' or 'o' or 'u' => 'a',
                'd' or 't' => 't',
                'k' or 'q' => 'k',
                >= 'a' and <= 'z' => c,
                _ => '\0',
            };
            // Vowels only count at the start: speech to text gets them wrong most.
            if (sound != '\0' && (sound != 'a' || sounds.Length == 0) && (sounds.Length == 0 || sounds[^1] != sound))
                sounds.Append(sound);
        }
        return sounds.ToString();
    }

    // Speech to text sometimes says a sentence again and again ("Jeeves, play X, Jeeves, play X, …"): once.
    public static string Unrepeated(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var plain = tokens.Select(t => NonWord().Replace(t.ToLowerInvariant(), "")).ToArray();
        for (var period = 3; period * 2 <= plain.Length; period++)
        {
            var repeats = true;
            for (var i = period; i < plain.Length && repeats; i++)
                repeats = plain[i] == plain[i - period];
            if (repeats)
                return string.Join(' ', tokens.Take(period)).TrimEnd(',', ' ');
        }
        return text;
    }

    private const double AlikePhrase = 0.75;
    private const double Margin = 0.1;

    private static readonly (string Phrase, VoiceIntent Intent)[] Phrases =
    [
        .. Say(VoiceIntent.Skip, "skip", "next", "skip it", "skip this", "skip this song", "next song", "skip that"),
        .. Say(VoiceIntent.Pause, "pause", "pause it", "pause the music", "hold on"),
        .. Say(VoiceIntent.Resume, "resume", "continue", "unpause", "keep going", "go on"),
        .. Say(VoiceIntent.Stop, "stop", "stop it", "stop the music", "be quiet", "leave", "go away"),
        .. Say(VoiceIntent.Louder, "louder", "turn it up", "volume up", "turn up"),
        .. Say(VoiceIntent.Quieter, "quieter", "softer", "turn it down", "volume down", "turn down"),
        .. Say(VoiceIntent.NowPlaying, "whats playing", "what is playing", "what song is this", "whats this song", "what is this song"),
        .. Say(VoiceIntent.Shuffle, "shuffle", "shuffle the queue"),
        .. Say(VoiceIntent.LoopTrack, "loop this", "loop this song", "repeat this song"),
        .. Say(VoiceIntent.LoopQueue, "loop the queue", "loop everything", "repeat the queue"),
        .. Say(VoiceIntent.LoopOff, "stop looping", "loop off", "dont loop"),
    ];

    private static IEnumerable<(string, VoiceIntent)> Say(VoiceIntent intent, params string[] phrases) => phrases.Select(p => (p, intent));

    // Lowercase words without punctuation ("What's" → "whats").
    private static List<string> Words(string text)
        => NonWord().Replace(text.ToLowerInvariant().Replace("'", "").Replace("’", ""), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    // 1 minus the edit distance over the longer length.
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"^(?:please |can you |could you |would you |will you )+|(?: please| now| right now| for me)+$")]
    private static partial Regex Filler();

    // "play the airhorn sound", "sound airhorn", "soundboard airhorn".
    [GeneratedRegex(@"^(?:play\w{0,2} )?(?:the |a |an )?(?<what>.+?) sound$|^sound(?:board)? (?<what>.+)$")]
    private static partial Regex SoundNamed();

    [GeneratedRegex(@"^quote(?: (?<who>.+))?$")]
    private static partial Regex QuoteThat();

    // Speech to text stretches the verbs ("playa", "plays", "queued").
    [GeneratedRegex(@"^(?:play\w{0,2}|put on|queue\w?|cue\w?|add)(?: me)? (?<what>.+?) (?:next|first|after this)$")]
    private static partial Regex QueueFirst();

    [GeneratedRegex(@"^(?:queue\w?|cue\w?|add)(?: me)? (?<what>.+?)(?: to the queue| at the end| last)?$")]
    private static partial Regex Queue();

    [GeneratedRegex(@"^(?:play\w{0,2}|put on)(?: me)? (?<what>.+?)(?: now| please)?$")]
    private static partial Regex Play();

    [GeneratedRegex(@"^(?:set )?(?:the )?volume (?:to )?(?<n>\d{1,3})(?: percent)?$")]
    private static partial Regex VolumeTo();
}
