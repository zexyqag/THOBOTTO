using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using THOBOTTO.Integrations;

using Whisper.net;
using Whisper.net.Ggml;

namespace THOBOTTO.Voice.Hearing;

// Turns a spoken sentence (16 kHz mono) into text, expecting the names given (the helpers' there).
public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] audio, IReadOnlyList<string> names, CancellationToken ct);
}

// A model file on its way: how much is here, of how much when known.
public sealed record Fetching(string What, long Done, long? Total);

// Whichever the owner picked on the Integrations page, both on this machine (nothing said leaves it): Whisper
// (the default), or Parakeet through sherpa-onnx. The chosen engine's model is fetched when it's chosen and at
// start, not when the first sentence waits for it, and the other engine is let go of.
public sealed class SpeechToText(IntegrationStore settings, LocalWhisper whisper, ParakeetSpeech parakeet, ILogger<SpeechToText> logger) : ISpeechToText, IHostedService
{
    public const string Whisper = "whisper";
    public const string Parakeet = "parakeet";

    private readonly ConcurrentQueue<(double Seconds, long Ms)> _recent = new();

    public string Engine => settings.Get(IntegrationStore.SpeechEngine) == Parakeet ? Parakeet : Whisper;

    // The last few sentences: how long each was, and how long it took (for the debug page).
    public IReadOnlyList<(double Seconds, long Ms)> Recent => [.. _recent];

    public Fetching? Downloading => whisper.Downloading ?? parakeet.Downloading;

    // These engines wait for cores busily: given more threads than they get cores (the language model and the
    // voices run on the same machine), a second of work takes many. A third of the cores by default.
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 3, 2, 6);

    public static int ThreadsOf(IntegrationStore settings)
        => int.TryParse(settings.Get(IntegrationStore.SpeechThreads), out var threads) ? Math.Clamp(threads, 1, Environment.ProcessorCount) : DefaultThreads;

    // Where speech models are kept.
    public static string ModelsFolder(IConfiguration config)
        => config["Listening:Models"] is { Length: > 0 } configured ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "models");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Prepare();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Prepare() => _ = PrepareAsync();

    private async Task PrepareAsync()
    {
        try
        {
            if (Engine == Parakeet)
            {
                await whisper.UnloadAsync();
                await parakeet.EnsureAsync();
            }
            else
            {
                await parakeet.UnloadAsync();
                await whisper.EnsureAsync();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning("Fetching the speech model failed: {Message}", ex.Message);
        }
    }

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

// Model files are written beside their place and moved in whole, so a fetch that broke off leaves nothing that
// passes for a model.
public static class ModelFile
{
    public static async Task SaveAsync(Stream source, string path, Action<long> progress, CancellationToken ct)
    {
        var partial = path + ".partial";
        await using (source)
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                progress(done += read);
            }
        }
        File.Move(partial, path, overwrite: true);
    }
}

// Whisper on this machine: nothing said leaves it. The model (the owner's pick; bigger is better and slower)
// is fetched once.
public sealed class LocalWhisper(IntegrationStore settings, IConfiguration config, ILogger<LocalWhisper> logger) : ISpeechToText, IDisposable
{
    public static readonly IReadOnlyList<(GgmlType Type, string Name)> Models =
    [
        (GgmlType.TinyEn, "Tiny: fastest, least accurate (~75 MB)"),
        (GgmlType.BaseEn, "Base: a good balance (~140 MB)"),
        (GgmlType.SmallEn, "Small: most accurate, a few times slower (~470 MB)"),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _fetches = new();
    private (GgmlType Type, Task Fetch)? _fetching;
    private (GgmlType Type, WhisperFactory Factory)? _loaded;

    public int Threads => SpeechToText.ThreadsOf(settings);

    public GgmlType Model => Enum.TryParse<GgmlType>(settings.Get(IntegrationStore.SpeechLocalModel), out var chosen) && Models.Any(m => m.Type == chosen) ? chosen : GgmlType.BaseEn;

    public Fetching? Downloading { get; private set; }

    public async Task<string> TranscribeAsync(float[] audio, IReadOnlyList<string> names, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var model = Model;
            if (_loaded?.Type != model)
            {
                await EnsureAsync().WaitAsync(ct);
                _loaded?.Factory.Dispose();
                _loaded = (model, WhisperFactory.FromPath(PathOf(model)));
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

    // The chosen model, fetched once however many ask; a fetch that failed is tried again by the next.
    public Task EnsureAsync()
    {
        var model = Model;
        lock (_fetches)
        {
            if (_fetching is { } fetching && fetching.Type == model && !fetching.Fetch.IsCompleted)
                return fetching.Fetch;
            if (Downloaded(model))
                return Task.CompletedTask;
            var fetch = FetchAsync(model);
            _fetching = (model, fetch);
            return fetch;
        }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _loaded?.Factory.Dispose();
            _loaded = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathOf(GgmlType type) => Path.Combine(SpeechToText.ModelsFolder(config), $"ggml-{type}.bin");

    private async Task FetchAsync(GgmlType type)
    {
        var path = PathOf(type);
        if (File.Exists(path))
            return;
        logger.LogInformation("Fetching the Whisper model {Model} to {Path}", type, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var what = $"Whisper {type}";
        try
        {
            Downloading = new(what, 0, null);
            await ModelFile.SaveAsync(await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type), path, done => Downloading = new(what, done, null), CancellationToken.None);
        }
        finally
        {
            Downloading = null;
        }
    }

    public void Dispose() => _loaded?.Factory.Dispose();
}
