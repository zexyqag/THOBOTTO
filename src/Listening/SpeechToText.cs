using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using THOBOTTO.Integrations;

using Whisper.net;
using Whisper.net.Ggml;

namespace THOBOTTO.Listening;

// Turns a spoken sentence (16 kHz mono) into text, expecting the names given (the helpers' there).
public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] audio, IReadOnlyList<string> names, CancellationToken ct);
}

// Whichever the owner picked on the Integrations page, both on this machine (nothing said leaves it): Whisper
// (the default), or Parakeet through sherpa-onnx.
public sealed class SpeechToText(IntegrationStore settings, LocalWhisper whisper, ParakeetSpeech parakeet) : ISpeechToText
{
    public const string Whisper = "whisper";
    public const string Parakeet = "parakeet";

    private readonly ConcurrentQueue<(double Seconds, long Ms)> _recent = new();

    public string Engine => settings.Get(IntegrationStore.SpeechEngine) == Parakeet ? Parakeet : Whisper;

    // The last few sentences: how long each was, and how long it took (for the debug page).
    public IReadOnlyList<(double Seconds, long Ms)> Recent => [.. _recent];

    // These engines wait for cores busily: given more threads than they get cores (the language model and the
    // voices run on the same machine), a second of work takes many. A third of the cores by default.
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 3, 2, 6);

    public static int ThreadsOf(IntegrationStore settings)
        => int.TryParse(settings.Get(IntegrationStore.SpeechThreads), out var threads) ? Math.Clamp(threads, 1, Environment.ProcessorCount) : DefaultThreads;

    // Where speech models are kept (downloaded on first use).
    public static string ModelsFolder(IConfiguration config)
        => config["Listening:Models"] is { Length: > 0 } configured ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "models");

    public async Task<string> TranscribeAsync(float[] audio, IReadOnlyList<string> names, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var text = await (Engine == Parakeet ? (ISpeechToText)parakeet : whisper).TranscribeAsync(audio, names, ct);
        _recent.Enqueue((audio.Length / 16_000.0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        while (_recent.Count > 20)
            _recent.TryDequeue(out _);
        return text;
    }
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

    public int Threads => SpeechToText.ThreadsOf(settings);

    public GgmlType Model => Enum.TryParse<GgmlType>(settings.Get(IntegrationStore.SpeechLocalModel), out var chosen) && Models.Any(m => m.Type == chosen) ? chosen : GgmlType.BaseEn;

    public async Task<string> TranscribeAsync(float[] audio, IReadOnlyList<string> names, CancellationToken ct)
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
            // The names in its prompt make Whisper expect them. Whisper reads 30 s windows;
            // a sentence is a few seconds, so it reads one that long (with room to spare): several times quicker.
            using var processor = _loaded.Value.Factory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt(names.Count > 0 ? string.Join(", ", names) + "." : "")
                .WithThreads(Threads)
                .WithAudioContextSize(AudioContext(audio.Length))
                .WithSingleSegment()
                .Build();
            var text = new StringBuilder();
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

    private string PathOf(GgmlType type) => Path.Combine(SpeechToText.ModelsFolder(config), $"ggml-{type}.bin");

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
