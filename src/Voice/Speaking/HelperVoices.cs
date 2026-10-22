using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using THOBOTTO.Helpers;
using THOBOTTO.Integrations;

namespace THOBOTTO.Voice.Speaking;

public static class VoiceEngines
{
    public const string Off = "off";
    public const string Piper = "piper";
    public const string Kokoro = "kokoro";
    public const string Cloud = "cloud";

    public const string KokoroUrl = "http://tts:8880/v1";
    public const string CloudUrl = "https://api.openai.com/v1";
    public const string CloudModel = "tts-1";
}

// What helpers sound like: the engine the owner picked on the Integrations page, and each personality's voice
// for it (else the plain helper's).
public sealed partial class HelperVoices(IntegrationStore store, PiperVoices piper, SpeechClient client, PersonalityBook personalities, TimeProvider time) : IVoiceCatalogue
{
    public const string ModuleId = "helper-voices";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    // The engine's voices, as last listed.
    private (string Engine, string? Url, IReadOnlyList<string> Voices, DateTimeOffset At)? _listed;

    // Longer replies are cut at a sentence, so the helper doesn't talk for ages.
    private const int MostCharacters = 300;

    public string Engine => store.Get(IntegrationStore.VoiceEngine) ?? VoiceEngines.Off;

    public bool On => Engine != VoiceEngines.Off;

    public bool Blends => Engine == VoiceEngines.Kokoro;

    // The engine's voices: empty when it can't be asked. A cloud service that lists none has OpenAI's.
    public async Task<IReadOnlyList<string>> VoicesAsync()
    {
        var (engine, url) = (Engine, store.Get(IntegrationStore.VoiceUrl));
        if (_listed is { } listed && listed.Engine == engine && listed.Url == url && time.GetUtcNow() - listed.At < Fresh)
            return listed.Voices;
        var voices = engine switch
        {
            VoiceEngines.Piper => await piper.VoicesAsync(),
            VoiceEngines.Kokoro => await client.VoicesAsync(url ?? VoiceEngines.KokoroUrl) ?? [],
            VoiceEngines.Cloud => await client.VoicesAsync(url ?? VoiceEngines.CloudUrl) ?? SpeechClient.OpenAiVoices,
            _ => [],
        };
        if (voices.Count > 0)
            _listed = (engine, url, voices, time.GetUtcNow());
        return voices;
    }

    // The engine's default: the plain helper's pick.
    public string DefaultVoice => PersonalityFile.Plain.Voices?.GetValueOrDefault(Engine) ?? SpeechClient.OpenAiVoices[0];

    public string VoiceOf(Personality? personality) => personality?.Voices.GetValueOrDefault(Engine) ?? DefaultVoice;

    public string PreviewLink(ulong guildId, long personalityId) => VoicePreview.Link(guildId, personalityId);

    // The personality's voice where the engine has it, else the default (a pick from another setup), shaped by its effects.
    public async Task<Spoken?> SayAsync(ulong guildId, HelperBot helper, string text, CancellationToken ct = default)
    {
        var worn = await personalities.WornAsync(guildId, helper.UserId);
        return await SayAsync(text, await UsableVoiceAsync(VoiceOf(worn)), VoiceEffects.From(worn?.Effects), ct);
    }

    // The voice when the engine has it (a Kokoro blend, part by part), else the default.
    public async Task<string> UsableVoiceAsync(string voice)
    {
        var known = await VoicesAsync();
        if (known.Count == 0)
            return voice;
        var parts = voice.Split('+');
        if (parts.Length > 1 && Engine != VoiceEngines.Kokoro)
            return DefaultVoice;
        return parts.All(p => known.Contains(Weight().Replace(p, ""))) ? voice : DefaultVoice;
    }

    // Short pieces come up again and again ("Skipped.", "One moment."): made once, kept a while.
    private const int ShortLine = 60;
    private const int MostKept = 300;
    private readonly ConcurrentDictionary<(string Engine, string Voice, string Text, double Speed), Spoken> _kept = new();

    // A line with its marks: said piece by piece, each at its speed and pitch with silences between, then shaped
    // by the effects. Pitch is resampling, which shortens, so the engine talks slower by the same factor first.
    public async Task<Spoken?> SayAsync(string text, string voice, VoiceEffects? effects = null, CancellationToken ct = default)
    {
        effects ??= VoiceEffects.None;
        var pieces = new List<(TimeSpan Pause, short[]? Samples)>();
        var rate = 0;
        foreach (var part in VoiceMarks.Parse(text))
        {
            var plain = Speakable(part.Text);
            short[]? samples = null;
            if (plain.Length > 0)
            {
                var factor = VoiceEffects.Factor(Math.Clamp(effects.Pitch + part.Pitch, -24, 24));
                var speed = Math.Clamp(effects.Speed * part.Speed / factor, 0.25, 4);
                if (await MadeAsync(plain, voice, speed, ct) is not { } spoken)
                    continue;
                rate = spoken.Rate;
                samples = VoiceEffects.Pitched(spoken.Samples, factor);
                if (part.Glitch)
                    samples = VoiceEffects.Glitched(samples, rate, Math.Max(effects.Glitch, 0.7), Seed(plain));
            }
            pieces.Add((part.Pause, samples));
        }
        if (rate == 0)
            return null;
        var all = new short[pieces.Sum(p => (int)(p.Pause.TotalSeconds * rate) + (p.Samples?.Length ?? 0))];
        var at = 0;
        foreach (var (pause, samples) in pieces)
        {
            at += (int)(pause.TotalSeconds * rate);
            if (samples is null)
                continue;
            samples.CopyTo(all, at);
            at += samples.Length;
        }
        return new(effects.Apply(all, rate, Seed(text)), rate);
    }

    private async Task<Spoken?> MadeAsync(string text, string voice, double speed, CancellationToken ct)
    {
        var key = (Engine, voice, text, speed);
        if (_kept.TryGetValue(key, out var kept))
            return kept;
        var spoken = await MakeAsync(text, voice, speed, ct);
        if (spoken is not null && text.Length <= ShortLine)
        {
            if (_kept.Count >= MostKept)
                _kept.Clear();
            _kept[key] = spoken;
        }
        return spoken;
    }

    // The same line sounds the same every time.
    private static int Seed(string text) => (int)text.Aggregate(2166136261u, (h, c) => (h ^ c) * 16777619u);

    private async Task<Spoken?> MakeAsync(string text, string voice, double speed, CancellationToken ct)
    {
        return Engine switch
        {
            VoiceEngines.Piper => await piper.SayAsync(text, voice, speed, ct),
            VoiceEngines.Kokoro => await client.SayAsync(store.Get(IntegrationStore.VoiceUrl) ?? VoiceEngines.KokoroUrl, null, "kokoro", text, voice, speed, ct),
            VoiceEngines.Cloud => await client.SayAsync(store.Get(IntegrationStore.VoiceUrl) ?? VoiceEngines.CloudUrl, store.Get(IntegrationStore.VoiceKey),
                store.Get(IntegrationStore.VoiceModel) ?? VoiceEngines.CloudModel, text, voice, speed, ct),
            _ => null,
        };
    }

    [GeneratedRegex(@"\(\d+(\.\d+)?\)$")]
    private static partial Regex Weight();

    // What's worth saying out loud from a chat line: names for mentions, no links, markup or custom emoji.
    public static string Speakable(string text) => SpeakableText.From(text, MostCharacters);
}
