using System.Runtime.InteropServices;
using System.Text;

using SherpaOnnx;

using THOBOTTO.Integrations;

namespace THOBOTTO.Listening;

// NVIDIA's Parakeet (TDT 0.6B v2, English, int8) through sherpa-onnx, on this machine, with the names it's told
// to expect (the helpers') boosted: in testing as accurate as Whisper's small model on the words, far better on
// the names, and about four times quicker. Downloaded once (about 670 MB), on first use; kept loaded (about 1 GB
// of memory) once used.
public sealed class ParakeetSpeech(IntegrationStore settings, IConfiguration config, ILogger<ParakeetSpeech> logger) : ISpeechToText, IDisposable
{
    // The model as sherpa-onnx publishes it, and NVIDIA's original (its tokenizer's word pieces and their scores,
    // which boosting names needs), both at a fixed revision.
    private const string Model = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/1ab9323565ddb038682214b292f588070a538ce2/";
    private const string Original = "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2/resolve/ae9ad07059c7c739ffaf932226a8fe64ae2620b0/parakeet-tdt-0.6b-v2.nemo";
    private static readonly string[] Files = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];
    private const string Vocab = "tokenizer.vocab";
    // How much an expected name is favoured (tested: 3 catches them all without hurting the rest).
    private const float NameBoost = 3f;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IntPtr Recognizer, int Threads)? _loaded;

    private string Folder => Path.Combine(SpeechToText.ModelsFolder(config), "parakeet-tdt-0.6b-v2-int8");

    public bool Downloaded => Files.Append(Vocab).All(f => File.Exists(Path.Combine(Folder, f)));

    public async Task<string> TranscribeAsync(float[] audio, IReadOnlyList<string> names, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var threads = SpeechToText.ThreadsOf(settings);
            if (_loaded?.Threads != threads)
            {
                await DownloadAsync(ct);
                Unload();
                var recognizerConfig = Config(threads);
                _loaded = (Native.Create(ref recognizerConfig), threads);
            }
            var recognizer = _loaded.Value.Recognizer;
            // Names are separated by "/" for it.
            var expected = string.Join("/", names.Select(n => n.Replace("/", " ").Trim()).Where(n => n.Length > 0));
            return await Task.Run(() =>
            {
                using var stream = new OfflineStream(Native.CreateStream(recognizer, Encoding.UTF8.GetBytes(expected + "\0")));
                stream.AcceptWaveform(16_000, audio);
                Native.Decode(recognizer, stream.Handle);
                return stream.Result.Text.Trim();
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OfflineRecognizerConfig Config(int threads)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(Folder, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(Folder, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(Folder, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(Folder, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.ModelingUnit = "bpe";
        config.ModelConfig.BpeVocab = Path.Combine(Folder, Vocab);
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;
        config.HotwordsScore = NameBoost;
        return config;
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        if (Downloaded)
            return;
        Directory.CreateDirectory(Folder);
        foreach (var file in Files.Where(f => !File.Exists(Path.Combine(Folder, f))))
        {
            logger.LogInformation("Downloading Parakeet's {File} to {Folder}", file, Folder);
            using var response = await Http.GetAsync(Model + file, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await SaveAsync(file, await response.Content.ReadAsStreamAsync(ct), ct);
        }
        if (!File.Exists(Path.Combine(Folder, Vocab)))
        {
            logger.LogInformation("Fetching Parakeet's word pieces from NVIDIA's model");
            await SaveAsync(Vocab, new MemoryStream(await FromOriginalAsync(Vocab, ct)), ct);
        }
    }

    private async Task SaveAsync(string file, Stream source, CancellationToken ct)
    {
        var partial = Path.Combine(Folder, file + ".partial");
        await using (source)
        await using (var target = File.Create(partial))
            await source.CopyToAsync(target, ct);
        File.Move(partial, Path.Combine(Folder, file), overwrite: true);
    }

    // One small file out of NVIDIA's model (a tar of 2.4 GB, mostly weights): its entries' headers are read one
    // by one, by range, until the one wanted; only that one's contents are fetched.
    private static async Task<byte[]> FromOriginalAsync(string wanted, CancellationToken ct)
    {
        long at = 0;
        string? longName = null;
        for (var entries = 0; entries < 100; entries++)
        {
            var header = await RangeAsync(at, 512, ct);
            if (header.Length < 512 || header.All(b => b == 0))
                break;
            var name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
            var size = Convert.ToInt64(Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ') is { Length: > 0 } octal ? octal : "0", 8);
            var type = (char)header[156];
            if (type is 'x' or 'g')
                longName = Encoding.UTF8.GetString(await RangeAsync(at + 512, size, ct)).Split('\n').Select(r => r.Split(" path=", 2)).FirstOrDefault(r => r.Length == 2)?[1];
            else
            {
                var path = longName ?? name;
                longName = null;
                if (path.EndsWith(wanted, StringComparison.Ordinal))
                    return await RangeAsync(at + 512, size, ct);
            }
            at += 512 + (size + 511) / 512 * 512;
        }
        throw new InvalidOperationException($"{wanted} wasn't found in NVIDIA's model.");
    }

    private static async Task<byte[]> RangeAsync(long from, long length, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Original);
        request.Headers.Range = new(from, from + length - 1);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private void Unload()
    {
        if (_loaded is { } loaded)
            Native.Destroy(loaded.Recognizer);
        _loaded = null;
    }

    public void Dispose() => Unload();

    // sherpa-onnx's C interface, where its .NET wrapper doesn't reach: a stream given the names to expect.
    private static class Native
    {
        [DllImport("sherpa-onnx-c-api", EntryPoint = "SherpaOnnxCreateOfflineRecognizer")]
        public static extern IntPtr Create(ref OfflineRecognizerConfig config);

        [DllImport("sherpa-onnx-c-api", EntryPoint = "SherpaOnnxDestroyOfflineRecognizer")]
        public static extern void Destroy(IntPtr recognizer);

        [DllImport("sherpa-onnx-c-api", EntryPoint = "SherpaOnnxCreateOfflineStreamWithHotwords")]
        public static extern IntPtr CreateStream(IntPtr recognizer, byte[] utf8Hotwords);

        [DllImport("sherpa-onnx-c-api", EntryPoint = "SherpaOnnxDecodeOfflineStream")]
        public static extern void Decode(IntPtr recognizer, IntPtr stream);
    }
}
