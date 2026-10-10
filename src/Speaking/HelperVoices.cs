using THOBOTTO.Helpers;
using THOBOTTO.Integrations;

namespace THOBOTTO.Speaking;

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
public sealed class HelperVoices(IntegrationStore store, PiperVoices piper, SpeechApi api, PersonalityBook personalities, TimeProvider time)
{
    public const string ModuleId = "voices";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    // The engine's voices, as last listed.
    private (string Engine, string? Url, IReadOnlyList<string> Voices, DateTimeOffset At)? _listed;

    // Longer replies are cut at a sentence, so the helper doesn't talk for ages.
    private const int MostCharacters = 300;

    public string Engine => store.Get(IntegrationStore.VoiceEngine) ?? VoiceEngines.Off;

    public bool On => Engine != VoiceEngines.Off;

    // The engine's voices: empty when it can't be asked. A cloud service that lists none has OpenAI's.
    public async Task<IReadOnlyList<string>> VoicesAsync()
    {
        var (engine, url) = (Engine, store.Get(IntegrationStore.VoiceUrl));
        if (_listed is { } listed && listed.Engine == engine && listed.Url == url && time.GetUtcNow() - listed.At < Fresh)
            return listed.Voices;
        IReadOnlyList<string> voices = engine switch
        {
            VoiceEngines.Piper => await piper.VoicesAsync(),
            VoiceEngines.Kokoro => await api.VoicesAsync(url ?? VoiceEngines.KokoroUrl) ?? [],
            VoiceEngines.Cloud => await api.VoicesAsync(url ?? VoiceEngines.CloudUrl) ?? SpeechApi.OpenAiVoices,
            _ => [],
        };
        if (voices.Count > 0)
            _listed = (engine, url, voices, time.GetUtcNow());
        return voices;
    }

    // The engine's default: the plain helper's pick.
    public string DefaultVoice => PersonalityFile.Plain.Voices?.GetValueOrDefault(Engine) ?? SpeechApi.OpenAiVoices[0];

    public string VoiceOf(Personality? personality) => personality?.Voices.GetValueOrDefault(Engine) ?? DefaultVoice;

    // The personality's voice where the engine has it, else the default (a pick from another setup).
    public async Task<Spoken?> SayAsync(ulong guildId, HelperBot helper, string text, CancellationToken ct = default)
    {
        var voice = VoiceOf(await personalities.WornAsync(guildId, helper.UserId));
        if (await VoicesAsync() is { Count: > 0 } known && !known.Contains(voice))
            voice = DefaultVoice;
        return await SayAsync(Speakable(text), voice, ct);
    }

    // Short lines come up again and again ("Skipped.", "One moment."): made once, kept a while.
    private const int ShortLine = 60;
    private const int MostKept = 300;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Engine, string Voice, string Text), Spoken> _kept = new();

    public async Task<Spoken?> SayAsync(string text, string voice, CancellationToken ct = default)
    {
        if (text.Length == 0)
            return null;
        var key = (Engine, voice, text);
        if (_kept.TryGetValue(key, out var kept))
            return kept;
        var spoken = await MakeAsync(text, voice, ct);
        if (spoken is not null && text.Length <= ShortLine)
        {
            if (_kept.Count >= MostKept)
                _kept.Clear();
            _kept[key] = spoken;
        }
        return spoken;
    }

    private async Task<Spoken?> MakeAsync(string text, string voice, CancellationToken ct)
    {
        return Engine switch
        {
            VoiceEngines.Piper => await piper.SayAsync(text, voice, ct),
            VoiceEngines.Kokoro => await api.SayAsync(store.Get(IntegrationStore.VoiceUrl) ?? VoiceEngines.KokoroUrl, null, "kokoro", text, voice, ct),
            VoiceEngines.Cloud => await api.SayAsync(store.Get(IntegrationStore.VoiceUrl) ?? VoiceEngines.CloudUrl, store.Get(IntegrationStore.VoiceKey),
                store.Get(IntegrationStore.VoiceModel) ?? VoiceEngines.CloudModel, text, voice, ct),
            _ => null,
        };
    }

    // What's worth saying out loud from a chat line: names for mentions, no links, markup or custom emoji.
    public static string Speakable(string text) => SpeakableText.From(text, MostCharacters);
}
