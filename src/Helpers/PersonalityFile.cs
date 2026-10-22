using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace THOBOTTO.Helpers;

// A personality as a file: what's exported and imported, and what the built-in templates are made of.
// { "name": "Jeeves", "color": "#5B6770", "avatar": "data:image/png;base64,…", "voices": { "kokoro": "bm_george" }, "effects": { "pitch": 12 }, "lines": { "joined": ["…"], … } }
// Lines may carry the bot's voice marks ([pause], [slow]…[/slow] and so on, VoiceMarks), never an engine's own syntax.
public sealed partial record PersonalityFile(string Name, string? Color, string? Avatar, Dictionary<string, List<string>> Lines, Dictionary<string, string>? Voices = null, List<string>? AlsoAnswersTo = null, Dictionary<string, double>? Effects = null, string? Engine = null)
{
    // The speech engines a personality may ask for; a server speaks with the ones it has.
    public static readonly string[] Engines = ["piper", "espeak", "kokoro", "cloud"];

    public const int MaxVoiceLength = 64;

    public const int MaxNameLength = 32;
    public const int MaxLineLength = 300;
    public const int MaxLinesPerMoment = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Emoji and quotes stay readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] AvatarTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    // The ready-made personalities, from Templates/*.json.
    public static IReadOnlyList<PersonalityFile> Templates { get; } = Load("Templates.").OrderBy(t => t.Name).ToList();

    // What a helper without a personality says.
    public static PersonalityFile Plain { get; } = Load("PlainLines.json").Single();

    [JsonIgnore]
    public int? ColorValue => Color is { } c && int.TryParse(c.TrimStart('#'), NumberStyles.HexNumber, null, out var value) ? value : null;

    [JsonIgnore]
    public (byte[] Bytes, string Type)? AvatarData => Avatar is { } a && DataUri().Match(a) is { Success: true } m
        ? (Convert.FromBase64String(m.Groups[2].Value), m.Groups[1].Value)
        : null;

    public static PersonalityFile From(Personality personality) => new(
        personality.Name,
        personality.Color is { } c ? $"#{c:X6}" : null,
        personality.Avatar is { } avatar ? $"data:{personality.AvatarType};base64,{Convert.ToBase64String(avatar)}" : null,
        Moments.All.Where(personality.Phrases.ContainsKey).ToDictionary(m => m, m => personality.Phrases[m].ToList()),
        personality.Voices.Count == 0 ? null : new(personality.Voices),
        personality.AlsoAnswersTo.Count == 0 ? null : [.. personality.AlsoAnswersTo],
        personality.Effects.Count == 0 ? null : new(personality.Effects),
        personality.Engine);

    public const int MostAlsoAnswersTo = 10;

    // "Javes, Eaves" (commas or lines) as names, each at most a name long, at most ten.
    public static List<string> ReadAlsoAnswersTo(string text)
        => [.. text.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length <= MaxNameLength).Distinct(StringComparer.OrdinalIgnoreCase).Take(MostAlsoAnswersTo)];

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    [JsonIgnore]
    public string FileName => (Regex.Replace(Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } slug ? slug : "personality") + ".json";

    // A file someone brings, checked against the same limits as editing; the problem when it doesn't fit.
    public static (PersonalityFile? File, string? Problem) Parse(string json)
    {
        PersonalityFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PersonalityFile>(json, Json);
        }
        catch (JsonException ex)
        {
            return (null, $"That isn't a personality file ({ex.Message.Split('.')[0]}).");
        }
        if (file is null || file.Lines is null || string.IsNullOrWhiteSpace(file.Name))
            return (null, "That isn't a personality file: it needs a name and lines.");

        var name = file.Name.Trim();
        if (name.Length > MaxNameLength)
            return (null, $"The name is longer than {MaxNameLength} characters.");
        if (file.Color is not null && (!HexColor().IsMatch(file.Color) || file.ColorValue is null))
            return (null, "The colour should look like #5B6770.");
        if (file.Lines.Keys.FirstOrDefault(k => !Moments.All.Contains(k)) is { } unknown)
            return (null, $"There's no moment called \"{unknown}\"; they are {string.Join(", ", Moments.All)}.");
        foreach (var (moment, lines) in file.Lines)
        {
            if (lines.Count > MaxLinesPerMoment)
                return (null, $"\"{moment}\" has more than {MaxLinesPerMoment} lines.");
            if (lines.Any(l => l.Length > MaxLineLength))
                return (null, $"A line for \"{moment}\" is longer than {MaxLineLength} characters.");
            if (lines.Select(VoiceMarks.Problem).FirstOrDefault(p => p is not null) is { } marks)
                return (null, $"A line for \"{moment}\": {marks}.");
        }
        if (file.Effects is { } effects && VoiceEffects.Problem(effects) is { } badEffect)
            return (null, badEffect);
        if (file.Engine is { } engine && !Engines.Contains(engine))
            return (null, $"\"{engine}\" isn't a speech engine; they are {string.Join(", ", Engines)}.");
        if (file.Avatar is not null)
        {
            if (DataUri().Match(file.Avatar) is not { Success: true } m || !AvatarTypes.Contains(m.Groups[1].Value))
                return (null, "The avatar should be a PNG, JPEG, GIF or WebP image as a data: address.");
            try
            {
                if (Convert.FromBase64String(m.Groups[2].Value).Length > PersonalityBook.MaxAvatarBytes)
                    return (null, $"The avatar is bigger than {PersonalityBook.MaxAvatarBytes / 1024 / 1024} MB.");
            }
            catch (FormatException)
            {
                return (null, "The avatar's image data is damaged.");
            }
        }

        if (file.AlsoAnswersTo is { } also && (also.Count > MostAlsoAnswersTo || also.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > MaxNameLength)))
            return (null, $"\"AlsoAnswersTo\" takes at most {MostAlsoAnswersTo} names of up to {MaxNameLength} characters.");
        if (file.Voices?.FirstOrDefault(v => !VoiceName().IsMatch(v.Key) || !VoiceName().IsMatch(v.Value) || v.Value.Length > MaxVoiceLength) is { Key: not null } bad)
            return (null, $"\"{bad.Key}\": \"{bad.Value}\" isn't a voice name (letters, digits, - _ . and :, at most {MaxVoiceLength}).");

        var cleaned = file.Lines.ToDictionary(p => p.Key, p => p.Value.Select(l => l.Trim()).Where(l => l.Length > 0).ToList());
        return (file with { Name = name, Lines = cleaned }, null);
    }

    private static IEnumerable<PersonalityFile> Load(string prefix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith($"THOBOTTO.Helpers.{prefix}")))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var (file, problem) = Parse(reader.ReadToEnd());
            yield return file ?? throw new InvalidOperationException($"{resource}: {problem}");
        }
    }

    // A voice, or Kokoro's blends ("af_sky+af_bella", "af_sky(2)+af_bella(1)").
    [GeneratedRegex(@"^[A-Za-z0-9_.:+()-]+$")]
    private static partial Regex VoiceName();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^data:([a-z]+/[a-z0-9.+-]+);base64,(.+)$", RegexOptions.Singleline)]
    private static partial Regex DataUri();
}
