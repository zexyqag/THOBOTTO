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
public sealed class HelperVoices(IntegrationStore store, PiperVoices piper, SpeechApi api, PersonalityBook personalities)
{
    public const string ModuleId = "voices";

    // Longer replies are cut at a sentence, so the helper doesn't talk for ages.
    private const int MostCharacters = 300;

    public string Engine => store.Get(IntegrationStore.VoiceEngine) ?? VoiceEngines.Off;

    public bool On => Engine != VoiceEngines.Off;

    public async Task<IReadOnlyList<string>> VoicesAsync() => Engine switch
    {
        VoiceEngines.Piper => await piper.VoicesAsync(),
        VoiceEngines.Kokoro => await api.VoicesAsync(store.Get(IntegrationStore.VoiceUrl) ?? VoiceEngines.KokoroUrl),
        VoiceEngines.Cloud => await api.VoicesAsync(store.Get(IntegrationStore.VoiceUrl) ?? VoiceEngines.CloudUrl),
        _ => [],
    };

    public string VoiceOf(Personality? personality)
        => personality?.Voices.GetValueOrDefault(Engine) ?? PersonalityFile.Plain.Voices?.GetValueOrDefault(Engine) ?? SpeechApi.OpenAiVoices[0];

    public async Task<Spoken?> SayAsync(ulong guildId, HelperBot helper, string text, CancellationToken ct = default)
        => await SayAsync(Speakable(text), VoiceOf(await personalities.WornAsync(guildId, helper.UserId)), ct);

    public async Task<Spoken?> SayAsync(string text, string voice, CancellationToken ct = default)
    {
        if (text.Length == 0)
            return null;
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
