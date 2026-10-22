using System.Globalization;
using System.Text.RegularExpressions;

namespace THOBOTTO.Helpers;

// The advanced way to shape a voice: an ffmpeg filter chain ("filters" in a personality's file, a field on its page),
// run on the whole line after the effects. Filters in order with a comma between, each with its options after an
// equals sign ("asetrate={rate}*0.9,aresample={rate},aecho=0.8:0.6:30:0.3"); {rate} stands for the sample rate.
// One straight chain of filters that shape sound: never one that reads a file, takes a second input or branches.
public static partial class VoiceFilters
{
    public const int MaxLength = 500;

    // ffmpeg's audio filters that take one sound in and give one out, needing nothing but their options.
    public static readonly IReadOnlyList<string> Allowed =
    [
        "acompressor", "acontrast", "acrusher", "adelay", "aderivative", "adynamicequalizer", "adynamicsmooth", "aecho", "aemphasis",
        "aeval", "aexciter", "afade", "afftdn", "afftfilt", "aformat", "afreqshift", "agate", "aintegral", "alimiter", "allpass",
        "anequalizer", "anlmdn", "aphaser", "aphaseshift", "apulsator", "aresample", "areverse", "asetnsamples", "asetrate",
        "asoftclip", "asubboost", "asubcut", "asupercut", "asuperpass", "asuperstop", "atempo", "atilt", "atrim", "bandpass",
        "bandreject", "bass", "biquad", "chorus", "compand", "crystalizer", "dcshift", "deesser", "dynaudnorm", "equalizer",
        "flanger", "highpass", "highshelf", "loudnorm", "lowpass", "lowshelf", "rubberband", "silenceremove", "speechnorm",
        "treble", "tremolo", "vibrato", "virtualbass", "volume",
    ];

    // Starting points for the page; each is a whole chain.
    public static readonly IReadOnlyList<(string Name, string Chain)> Presets =
    [
        ("Mechanical (a machine's voice: flat, metallic, with a short room)", "afftfilt=real='hypot(re,im)*sin(0)':imag='hypot(re,im)*cos(0)':win_size=512:overlap=0.75,asetrate={rate}*0.85,aresample={rate},lowpass=f=3200,aecho=0.8:0.6:40:0.3"),
        ("Radio (a narrow, crackling band)", "highpass=f=400,lowpass=f=2800,acrusher=bits=10:mode=log:aa=1,acompressor=threshold=0.1:ratio=6:attack=5:release=50,volume=1.4"),
        ("Megaphone (loud, harsh, clipped)", "highpass=f=500,lowpass=f=3500,acompressor=threshold=0.05:ratio=12:attack=2:release=40,asoftclip=type=hard:threshold=0.4,volume=1.8"),
        ("Telephone (thin and distant)", "highpass=f=300,lowpass=f=3400,aresample={rate},acompressor=threshold=0.2:ratio=4,volume=1.2"),
        ("Cathedral (a long, wide echo)", "aecho=0.8:0.88:60|120|240:0.4|0.3|0.2,lowpass=f=6000"),
        ("Underwater (muffled and wobbling)", "lowpass=f=700,vibrato=f=4:d=0.3,volume=1.3"),
        ("Chipmunk (fast and high)", "asetrate={rate}*1.4,aresample={rate},atempo=0.85"),
        ("Giant (slow and deep)", "asetrate={rate}*0.7,aresample={rate},atempo=1.2,bass=g=6"),
    ];

    // The chain's filters by name, in order; a quoted option (an expression with commas) stays with its filter.
    public static IReadOnlyList<string> Names(string chain)
        => [.. Split(chain).Select(f => f.Split('=', 2)[0].Trim())];

    // What's wrong with a chain, or null when it's one straight chain of allowed filters.
    public static string? Problem(string chain)
    {
        if (chain.Length > MaxLength)
            return $"The filters are longer than {MaxLength} characters.";
        if (chain.IndexOfAny([';', '[', ']', '\\', '"', '\n', '\r']) >= 0)
            return "The filters are one straight chain: filters in order with a comma between, no labels, branches or backslashes.";
        if (chain.Count(c => c == '\'') % 2 != 0)
            return "A quote in the filters isn't closed.";
        if (!WithRate(chain, 1).All(c => char.IsAsciiLetterOrDigit(c) || "=:.,|_+-*/()%' ".Contains(c)))
            return "The filters may use letters, digits, spaces, {rate} and = : . , | _ + - * / ( ) % and quotes.";
        foreach (var name in Names(chain))
        {
            if (name.Length == 0)
                return "A filter in the chain has no name.";
            if (!Allowed.Contains(name))
                return $"\"{name}\" isn't a filter that may be used here; they are {string.Join(", ", Allowed)}.";
        }
        return null;
    }

    public static string WithRate(string chain, int rate) => chain.Replace("{rate}", rate.ToString(CultureInfo.InvariantCulture));

    // The chain at its commas, except inside quotes.
    private static IEnumerable<string> Split(string chain)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < chain.Length; i++)
        {
            if (chain[i] == '\'')
                quoted = !quoted;
            else if (chain[i] == ',' && !quoted)
            {
                yield return chain[start..i];
                start = i + 1;
            }
        }
        yield return chain[start..];
    }
}
