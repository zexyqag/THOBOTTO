using System.Text.Json;
using System.Text.RegularExpressions;

using THOBOTTO.Reposts;

namespace THOBOTTO.Kaomoji;

// A face and what finds it. Key: what picking it says, "l12" for the library's, "s5" for a server's own.
public sealed record Face(string Key, string Text, string Name, IReadOnlyList<string> Tags)
{
    public IReadOnlyList<string> NameWords { get; } = KaomojiLibrary.WordsOf(Name);

    public IReadOnlyList<string> Words { get; } = KaomojiLibrary.WordsOf(string.Join(' ', Tags.Prepend(Name)));
}

// The faces everyone has: Library.jsonl, from FontVibe's kaomoji-dataset (CC BY 4.0; see NOTICE), led by the
// classics under the names people search them by.
public static partial class KaomojiLibrary
{
    // Their name, then other words for them.
    private static readonly (string Text, string Names)[] Classics =
    [
        ("¯\\_(ツ)_/¯", "Shrug, idk, whatever"),
        ("( ͡° ͜ʖ ͡°)", "Lenny, lenny face, smirk"),
        ("ಠ_ಠ", "Look of disapproval, disapprove, stare, unimpressed"),
        ("(╯°□°)╯︵ ┻━┻", "Table flip, tableflip, flip, rage"),
        ("┬─┬ノ( º _ ºノ)", "Table put back, unflip, calm down"),
        ("(ง •̀_•́)ง", "Fight me, fists, bring it"),
        ("ʕ•ᴥ•ʔ", "Bear"),
        ("(づ｡◕‿‿◕｡)づ", "Hug, hugs"),
        ("༼ つ ◕_◕ ༽つ", "Gimme, give"),
        ("(⌐■_■)", "Deal with it, sunglasses, cool"),
        ("( •_•)>⌐■-■", "Putting on sunglasses, deal with it"),
        ("(ﾉ◕ヮ◕)ﾉ*:･ﾟ✧", "Magic, sparkles, yay"),
        ("ಥ_ಥ", "Crying, cry, tears"),
        ("(╥﹏╥)", "Sobbing, sob, cry"),
        ("ᕦ(ò_óˇ)ᕤ", "Flex, strong, muscles"),
        ("(☞ﾟヮﾟ)☞", "Finger guns, you"),
        ("ლ(ಠ益ಠლ)", "Why, y u no"),
        ("┻━┻ ︵ヽ(`Д´)ﾉ︵ ┻━┻", "Double table flip, tableflip"),
        ("(¬‿¬)", "Smug"),
        ("(°ロ°)☝", "Actually, well actually"),
        ("ᕕ( ᐛ )ᕗ", "Off I go, strut"),
        ("(=^･ω･^=)", "Cat, kitty"),
        ("(´・ω・`)", "Shobon, oh no, disappointed"),
        ("( ˘ ³˘)♥", "Kiss"),
        ("(◔_◔)", "Eye roll, roll eyes"),
        ("(⊙_☉)", "What, huh, confused"),
        ("(｡•̀ᴗ-)✧", "Wink"),
        ("(っ˘ڡ˘ς)", "Yummy, delicious"),
        ("(￣o￣) zzZ", "Sleeping, sleep, zzz"),
        ("ヽ(´▽`)/", "Cheer, yay"),
        ("(^_^)/", "Wave, hi, bye"),
        ("(눈_눈)", "Side eye, suspicious, sus"),
        ("ʕノ•ᴥ•ʔノ ︵ ┻━┻", "Bear table flip, tableflip"),
    ];

    public static IReadOnlyList<Face> All { get; } = Load();

    private static List<Face> Load()
    {
        using var reader = new StreamReader(typeof(KaomojiLibrary).Assembly.GetManifestResourceStream("THOBOTTO.Kaomoji.Library.jsonl")!);
        var read = new List<(string Text, string Name, string[] Tags)>();
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            var fields = JsonSerializer.Deserialize<string[]>(line)!;
            read.Add((fields[0], fields[1], fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries)));
        }
        var faces = new List<Face>();
        foreach (var (text, names) in Classics)
        {
            var given = names.Split(", ");
            var tags = given.Skip(1).Select(n => n.ToLowerInvariant()).ToList();
            if (read.FirstOrDefault(r => r.Text == text) is { Text: not null } known)
                tags.AddRange(known.Tags.Prepend(known.Name.ToLowerInvariant()).Where(t => t.Length > 0 && !tags.Contains(t)));
            faces.Add(new($"l{faces.Count}", text, given[0], tags));
        }
        var classic = Classics.Select(c => c.Text).ToHashSet();
        foreach (var (text, name, tags) in read.Where(r => !classic.Contains(r.Text)))
            faces.Add(new($"l{faces.Count}", text, name.Length > 0 ? name : char.ToUpperInvariant(tags[0][0]) + tags[0][1..], tags));
        return faces;
    }

    // Best first: each word searched for starts a word of a face's name or tags, or is in the face itself; whole
    // words count more (in its name most), the whole name or a whole tag more still, then the order given.
    public static IEnumerable<Face> Search(IReadOnlyList<Face> faces, string search) => Scored(faces, search).Select(x => x.Face);

    private static IEnumerable<(Face Face, int Score)> Scored(IReadOnlyList<Face> faces, string search)
    {
        var typed = search.Trim();
        if (typed.Length == 0)
            return faces.Select(f => (f, 0));
        var words = WordsOf(typed);
        var whole = string.Join(' ', words);
        return faces.Select((face, i) => (Face: face, Order: i, Score: Score(face, typed, words, whole)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)
            .Select(x => (x.Face, x.Score));
    }

    private static int Score(Face face, string typed, IReadOnlyList<string> words, string whole)
    {
        if (face.Text == typed)
            return 100;
        if (words.Count == 0)
            return face.Text.Contains(typed, StringComparison.Ordinal) ? 1 : 0;
        var score = face.Name.Equals(whole, StringComparison.OrdinalIgnoreCase) || face.Tags.Contains(whole) ? 10 : 0;
        foreach (var word in words)
        {
            var best = face.NameWords.Contains(word) ? 4
                : face.Words.Contains(word) ? 3
                : face.Words.Any(w => w.StartsWith(word, StringComparison.Ordinal)) ? 2
                : face.Text.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1
                : 0;
            if (best == 0)
                return 0;
            score += best;
        }
        return score;
    }

    // Someone's favourites (their texts, most used first) ahead of the rest, each face once.
    public static IEnumerable<Face> Favoured(IEnumerable<Face> found, IReadOnlyList<string> favourites)
    {
        var rank = favourites.Index().ToDictionary(f => f.Item, f => f.Index);
        return found.DistinctBy(f => f.Text).OrderBy(f => rank.GetValueOrDefault(f.Text, int.MaxValue));
    }

    // What /kaomoji was given: one picked from its list, or what its list would offer first for what was typed.
    public static Face? Pick(IReadOnlyList<Face> faces, string given, IReadOnlyList<string> favourites)
        => faces.FirstOrDefault(f => f.Key == given) ?? Favoured(Search(faces, given), favourites).FirstOrDefault();

    // For a word written in a message, where no list is seen: the best match, a favourite among equals.
    public static Face? Best(IReadOnlyList<Face> faces, string search, IReadOnlyList<string> favourites)
    {
        var scored = Scored(faces, search).ToList();
        return scored.Count == 0 ? null : Favoured(scored.TakeWhile(x => x.Score == scored[0].Score).Select(x => x.Face), favourites).First();
    }

    public static bool HasWords(string text) => text.Contains("kao:", StringComparison.OrdinalIgnoreCase);

    // Each kao:word outside code (kao:shrug, kao:table_flip) becomes its face, escaped; a word that finds none stays.
    public static (string Text, IReadOnlyList<Face> Used) Fill(string text, IReadOnlyList<Face> faces, IReadOnlyList<string> favourites)
    {
        var used = new List<Face>();
        return (RepostText.OutsideCode(text, Words), used);

        string Words(string part) => Word().Replace(part, m =>
        {
            if (Best(faces, m.Groups[1].Value.Replace('_', ' ').Replace('-', ' '), favourites) is not { } face)
                return m.Value;
            used.Add(face);
            return Escaped(face.Text);
        });
    }

    // The face in a message, to save: a known one in it (the longest), else the longest run of face-like words (half or
    // more of them brackets or symbols beyond plain punctuation), else the whole message.
    public static (string Text, Face? Known) Guess(string message, IReadOnlyList<Face> faces)
    {
        var text = message.Trim();
        if (faces.Where(f => f.Text.Length >= 3 && text.Contains(f.Text, StringComparison.Ordinal)).MaxBy(f => f.Text.Length) is { } known)
            return (known.Text, known);
        var best = "";
        var run = new List<string>();
        foreach (var word in text.Split(' ').Append(""))
        {
            if (word.Length > 0 && FaceLike(word))
            {
                run.Add(word);
                continue;
            }
            if (string.Join(' ', run) is { Length: > 0 } joined && joined.Length > best.Length)
                best = joined;
            run.Clear();
        }
        return (best.Length > 0 ? best : text, null);

        static bool FaceLike(string word)
            => !word.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !word.StartsWith('<')
                && 2 * word.Count(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or ',' or '!' or '?' or '\'' or '"')) >= word.Length;
    }

    // As it looks, where Discord would read ¯\_(ツ)_/¯'s backslash and underscores as markdown.
    public static string Escaped(string text) => Markdown().Replace(text, @"\$0");

    public static IReadOnlyList<string> WordsOf(string text) => [.. NonWord().Split(text.ToLowerInvariant()).Where(w => w.Length > 0).Distinct()];

    [GeneratedRegex(@"[\\*_~`|<>#\-\[\]]")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_/:])kao:([\p{L}\p{N}_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Word();
}
