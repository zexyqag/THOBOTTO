using System.Net.Http.Headers;
using System.Text.Json;

using THOBOTTO.Integrations;

using Whisper.net;
using Whisper.net.Ggml;

namespace THOBOTTO.Listening;

// Turns a spoken sentence (16 kHz mono) into text.
public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] audio, string hint, CancellationToken ct);
}

// Whichever the owner picked on the Integrations page: Whisper here (the default), or a cloud service.
public sealed class SpeechToText(IntegrationStore settings, LocalWhisper local, CloudSpeech cloud) : ISpeechToText
{
    public bool UsesCloud => settings.Get(IntegrationStore.SpeechEngine) == "cloud";

    public Task<string> TranscribeAsync(float[] audio, string hint, CancellationToken ct)
        => (UsesCloud ? (ISpeechToText)cloud : local).TranscribeAsync(audio, hint, ct);
}

// Whisper on this machine: nothing said leaves it. The model (the owner's pick; bigger is better and slower)
// is downloaded once, on first use.
public sealed class LocalWhisper(IntegrationStore settings, IConfiguration config, ILogger<LocalWhisper> logger) : ISpeechToText, IDisposable
{
    public static readonly IReadOnlyList<(GgmlType Type, string Name)> Models =
    [
        (GgmlType.TinyEn, "Tiny: fastest, least accurate (~75 MB)"),
        (GgmlType.BaseEn, "Base: a good balance (~140 MB)"),
        (GgmlType.SmallEn, "Small: most accurate, a few times slower (~470 MB)"),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (GgmlType Type, WhisperFactory Factory)? _loaded;

    public GgmlType Model => Enum.TryParse<GgmlType>(settings.Get(IntegrationStore.SpeechLocalModel), out var chosen) && Models.Any(m => m.Type == chosen) ? chosen : GgmlType.BaseEn;

    public async Task<string> TranscribeAsync(float[] audio, string hint, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var model = Model;
            if (_loaded?.Type != model)
            {
                _loaded?.Factory.Dispose();
                _loaded = (model, WhisperFactory.FromPath(await ModelPathAsync(model, ct)));
            }
            // The hint (the helpers' names, the command words) makes Whisper expect them. Whisper reads 30 s windows;
            // a sentence is a few seconds, so it reads one that long (with room to spare): several times quicker.
            using var processor = _loaded.Value.Factory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt(hint)
                .WithThreads(Environment.ProcessorCount)
                .WithAudioContextSize(AudioContext(audio.Length))
                .WithSingleSegment()
                .Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, ct))
                text.Append(segment.Text);
            return text.ToString().Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Whisper's window is 1500 steps for 30 s; this many for the audio, plus 2 s.
    public static int AudioContext(int samples) => Math.Clamp((int)Math.Ceiling((samples / 16_000.0 + 2) / 30 * 1500), 256, 1500);

    public bool Downloaded(GgmlType type) => File.Exists(PathOf(type));

    private string PathOf(GgmlType type) => Path.Combine(
        config["Listening:Models"] is { Length: > 0 } configured ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "models"),
        $"ggml-{type}.bin");

    private async Task<string> ModelPathAsync(GgmlType type, CancellationToken ct)
    {
        var path = PathOf(type);
        var directory = Path.GetDirectoryName(path)!;
        if (File.Exists(path))
            return path;

        logger.LogInformation("Downloading the Whisper model {Model} to {Path}", type, path);
        Directory.CreateDirectory(directory);
        var partial = path + ".partial";
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: ct))
        await using (var file = File.Create(partial))
            await source.CopyToAsync(file, ct);
        File.Move(partial, path, overwrite: true);
        return path;
    }

    public void Dispose() => _loaded?.Factory.Dispose();
}

// A cloud service with the OpenAI-style transcription API (Groq, OpenAI, …): faster and more accurate,
// but what's said goes to that company.
public sealed class CloudSpeech(IntegrationStore settings) : ISpeechToText
{
    public const string DefaultUrl = "https://api.groq.com/openai/v1";
    public const string DefaultModel = "whisper-large-v3-turbo";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> TranscribeAsync(float[] audio, string hint, CancellationToken ct)
    {
        if (settings.Get(IntegrationStore.SpeechCloudKey) is not { Length: > 0 } key)
            throw new InvalidOperationException("No API key for the cloud speech service.");
        var url = settings.Get(IntegrationStore.SpeechCloudUrl) is { Length: > 0 } custom ? custom.TrimEnd('/') : DefaultUrl;
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Wav(audio)) { Headers = { ContentType = new("audio/wav") } }, "file", "sentence.wav" },
            { new StringContent(settings.Get(IntegrationStore.SpeechCloudModel) is { Length: > 0 } model ? model : DefaultModel), "model" },
            { new StringContent("en"), "language" },
            { new StringContent(hint), "prompt" },
            { new StringContent("json"), "response_format" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The speech service said {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }

    // 16 kHz mono, 16-bit.
    public static byte[] Wav(float[] audio)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + audio.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(16_000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(audio.Length * 2);
        foreach (var sample in audio)
            writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        return stream.ToArray();
    }
}
