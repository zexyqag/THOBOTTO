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
    public const string Espeak = "espeak";

    // Always there, needing no setup.
    public static readonly string[] InImage = [Piper, Espeak];

    public static string Label(string engine) => engine switch
    {
        Piper => "Piper (in the image)",
        Espeak => "eSpeak NG (in the image, the robot voice)",
        Kokoro => "Kokoro",
        Cloud => "the cloud service",
        _ => engine,
    };

    public const string KokoroUrl = "http://tts:8880/v1";
    public const string CloudUrl = "https://api.openai.com/v1";
    public const string CloudModel = "tts-1";
}

// A helper's line as sound, with the engine and voice that said it.
public sealed record HelperSpoken(Spoken Sound, string Engine, string Voice);

// What helpers sound like: the engine the owner picked on the Integrations page, and each personality's voice
// for it (else the plain helper's).
public sealed partial class HelperVoices(IntegrationStore store, PiperVoices piper, EspeakVoices espeak, SpeechClient client, PersonalityBook personalities, ISoundSource sounds, FfmpegFilter filter, TimeProvider time) : IVoiceCatalogue
{
    public const string ModuleId = "helper-voices";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    // Each engine's voices, as last listed.
    private readonly ConcurrentDictionary<string, (string? Url, IReadOnlyList<string> Voices, DateTimeOffset At)> _listed = new();

    // Longer replies are cut at a sentence, so the helper doesn't talk for ages.
    private const int MostCharacters = 300;

    public string Engine => store.Get(IntegrationStore.VoiceEngine) ?? VoiceEngines.Off;

    public bool On => Engine != VoiceEngines.Off;

    public bool Blends(string engine) => engine == VoiceEngines.Kokoro;

    // The engines a personality may ask for here: the ones in the image, and the server's pick.
    public IReadOnlyList<(string Id, string Name)> Engines => !On ? [] : [.. VoiceEngines.InImage.Append(Engine).Distinct().Select(e => (e, VoiceEngines.Label(e)))];

    public string EngineName(string engine) => VoiceEngines.Label(engine);

    public string EngineFor(Personality? personality) => Choose(personality?.Engine, Engine);

    public string EngineFor(string? preferred) => Choose(preferred, Engine);

    // eSpeak's variants change any of its voices ("en-us+m3"); the other engines have none.
    public IReadOnlyList<string> VariantsOf(string engine) => engine == VoiceEngines.Espeak ? EspeakVoices.Variants : [];

    // A personality's preference when it's here (in the image, or the server's pick), else the server's pick.
    public static string Choose(string? preferred, string serverEngine)
        => preferred is { } wanted && (VoiceEngines.InImage.Contains(wanted) || wanted == serverEngine) ? wanted : serverEngine;

    public Task<IReadOnlyList<string>> VoicesAsync() => VoicesAsync(Engine);

    // An engine's voices: empty when it can't be asked. A cloud service that lists none has OpenAI's.
    public async Task<IReadOnlyList<string>> VoicesAsync(string engine)
    {
        var url = store.Get(IntegrationStore.VoiceUrl);
        if (_listed.TryGetValue(engine, out var listed) && listed.Url == url && time.GetUtcNow() - listed.At < Fresh)
            return listed.Voices;
        var voices = engine switch
        {
            VoiceEngines.Piper => await piper.VoicesAsync(),
            VoiceEngines.Espeak => await espeak.VoicesAsync(),
            VoiceEngines.Kokoro => await client.VoicesAsync(url ?? VoiceEngines.KokoroUrl) ?? [],
            VoiceEngines.Cloud => await client.VoicesAsync(url ?? VoiceEngines.CloudUrl) ?? SpeechClient.OpenAiVoices,
            _ => [],
        };
        if (voices.Count > 0)
            _listed[engine] = (url, voices, time.GetUtcNow());
        return voices;
    }

    // An engine's default: the plain helper's pick.
    public string DefaultVoice(string engine) => PersonalityFile.Plain.Voices?.GetValueOrDefault(engine) ?? SpeechClient.OpenAiVoices[0];

    // The personality's voice for the engine it speaks through here.
    public string VoiceOf(Personality? personality)
    {
        var engine = EngineFor(personality);
        return personality?.Voices.GetValueOrDefault(engine) ?? DefaultVoice(engine);
    }

    public string PreviewLink(ulong guildId, long personalityId) => VoicePreview.Link(guildId, personalityId);

    // Through the engine the personality speaks with here, in its voice where the engine has it (else the default,
    // a pick from another setup), shaped by its effects and filters; with which engine and voice that was.
    public async Task<HelperSpoken?> SayAsync(ulong guildId, HelperBot helper, string text, CancellationToken ct = default)
    {
        var worn = await personalities.WornAsync(guildId, helper.UserId);
        var engine = EngineFor(worn);
        var voice = await UsableVoiceAsync(engine, VoiceOf(worn));
        return await SayAsync(guildId, text, engine, voice, VoiceEffects.From(worn?.Effects), worn?.Filters, ct) is { } spoken ? new(spoken, engine, voice) : null;
    }

    // The voice when the engine has it (a Kokoro blend part by part, an eSpeak variant by its base), else the default.
    public async Task<string> UsableVoiceAsync(string engine, string voice)
    {
        var known = await VoicesAsync(engine);
        if (known.Count == 0)
            return voice;
        var parts = voice.Split('+');
        if (parts.Length > 1 && engine == VoiceEngines.Espeak)
            return known.Contains(parts[0]) ? voice : DefaultVoice(engine);
        if (parts.Length > 1 && engine != VoiceEngines.Kokoro)
            return DefaultVoice(engine);
        return parts.All(p => known.Contains(Weight().Replace(p, ""))) ? voice : DefaultVoice(engine);
    }

    // Short pieces come up again and again ("Skipped.", "One moment."): made once, kept a while.
    private const int ShortLine = 60;
    private const int MostKept = 300;
    private readonly ConcurrentDictionary<(string Engine, string Voice, string Text, double Speed), Spoken> _kept = new();

    // A line with its marks: said piece by piece, each at its speed and pitch with silences between, sounds
    // dropped in where the server has them (or Discord's defaults), then shaped by the effects or the filter chain
    // (a chain that won't run is skipped). Pitch is resampling, which shortens, so the engine talks slower by the
    // same factor first.
    public async Task<Spoken?> SayAsync(ulong guildId, string text, string engine, string voice, VoiceEffects? effects = null, string? filters = null, CancellationToken ct = default)
    {
        // A voice is shaped by its effects or by its filters, never both.
        effects = filters is { Length: > 0 } ? VoiceEffects.None : effects ?? VoiceEffects.None;
        var pieces = new List<(TimeSpan Pause, short[]? Samples, int Rate)>();
        var rate = 0;
        foreach (var part in VoiceMarks.Parse(text))
        {
            if (part.Sound is { } name)
            {
                var clip = await sounds.SoundAsync(guildId, name, ct);
                pieces.Add((part.Pause, clip?.Samples, clip?.Rate ?? 0));
                continue;
            }
            var plain = Speakable(part.Text);
            short[]? samples = null;
            if (plain.Length > 0)
            {
                var factor = VoiceEffects.Factor(Math.Clamp(effects.Pitch + part.Pitch, -24, 24));
                var speed = Math.Clamp(effects.Speed * part.Speed / factor, 0.25, 4);
                if (await MadeAsync(engine, plain, voice, speed, ct) is not { } spoken)
                    continue;
                rate = spoken.Rate;
                samples = VoiceEffects.Pitched(spoken.Samples, factor);
                if (part.Glitch)
                    samples = VoiceEffects.Glitched(samples, rate, Math.Max(effects.Stutter, 0.7), Math.Max(effects.Dropout, 0.7), Math.Max(effects.Crackle, 0.7), Seed(plain));
            }
            pieces.Add((part.Pause, samples, rate));
        }
        // The engine's rate carries the line; a line of sounds alone goes at the first sound's.
        if (rate == 0)
            rate = pieces.FirstOrDefault(p => p.Rate > 0).Rate;
        if (rate == 0)
            return null;
        var parts = pieces.Select(p => (p.Pause, Samples: p.Samples is null ? null : Pcm.Resampled(p.Samples, p.Rate, rate))).ToList();
        var all = new short[parts.Sum(p => (int)(p.Pause.TotalSeconds * rate) + (p.Samples?.Length ?? 0))];
        var at = 0;
        foreach (var (pause, samples) in parts)
        {
            at += (int)(pause.TotalSeconds * rate);
            if (samples is null)
                continue;
            samples.CopyTo(all, at);
            at += samples.Length;
        }
        // Leveled before the shaping, so an effect's amount means the same whichever engine spoke and however loud,
        // and after it, since shaping takes level away (a filter, a robot tone) or adds it (static, echo).
        var shaped = new Spoken(effects.Apply(Pcm.Leveled(all, 100), rate, Seed(text)), rate);
        if (filters is { Length: > 0 } && await filter.ApplyAsync(shaped, filters, ct) is { } filtered)
            shaped = filtered;
        return new(Pcm.Leveled(shaped.Samples, 100), rate);
    }

    private async Task<Spoken?> MadeAsync(string engine, string text, string voice, double speed, CancellationToken ct)
    {
        var key = (engine, voice, text, speed);
        if (_kept.TryGetValue(key, out var kept))
            return kept;
        var spoken = await MakeAsync(engine, text, voice, speed, ct);
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

    private async Task<Spoken?> MakeAsync(string engine, string text, string voice, double speed, CancellationToken ct)
    {
        return engine switch
        {
            VoiceEngines.Piper => await piper.SayAsync(text, voice, speed, ct),
            VoiceEngines.Espeak => await espeak.SayAsync(text, voice, speed, ct),
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
