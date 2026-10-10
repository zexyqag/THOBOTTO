using System.Diagnostics;
using System.Text.Json;

namespace THOBOTTO.Speaking;

// Piper, run on this server: a voice is downloaded (from the piper-voices collection) the first time it's used.
public sealed class PiperVoices(IConfiguration config, ILogger<PiperVoices> logger)
{
    private const string Collection = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly SemaphoreSlim _downloading = new(1, 1);
    // Voice → its files in the collection, for English voices.
    private Dictionary<string, string>? _index;

    private string Program => config["Speaking:Piper"] ?? "piper";

    private string Folder => Path.Combine(config["Listening:Models"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "models"), "piper");

    public async Task<IReadOnlyList<string>> VoicesAsync() => (await IndexAsync()).Keys.Order().ToList();

    public async Task<Spoken?> SayAsync(string text, string voice, CancellationToken ct)
    {
        if (await ModelAsync(voice) is not { } model)
            return null;
        using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(model + ".json", ct));
        var rate = meta.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        var start = new ProcessStartInfo(Program, ["--model", model, "--output_raw", "--quiet"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var piper = Process.Start(start)!;
        await piper.StandardInput.WriteLineAsync(text.ReplaceLineEndings(" "));
        piper.StandardInput.Close();
        using var audio = new MemoryStream();
        await piper.StandardOutput.BaseStream.CopyToAsync(audio, ct);
        await piper.WaitForExitAsync(ct);
        if (piper.ExitCode != 0)
        {
            logger.LogWarning("Piper failed: {Error}", await piper.StandardError.ReadToEndAsync(ct));
            return null;
        }
        return new(Pcm.FromBytes(audio.ToArray()), rate);
    }

    private async Task<string?> ModelAsync(string voice)
    {
        var model = Path.Combine(Folder, voice + ".onnx");
        if (File.Exists(model) && File.Exists(model + ".json"))
            return model;
        if (!(await IndexAsync()).TryGetValue(voice, out var path))
            return null;
        await _downloading.WaitAsync();
        try
        {
            if (File.Exists(model) && File.Exists(model + ".json"))
                return model;
            Directory.CreateDirectory(Folder);
            logger.LogInformation("Downloading the Piper voice {Voice}", voice);
            foreach (var (from, to) in new[] { (path, model), (path + ".json", model + ".json") })
            {
                await using var download = await Http.GetStreamAsync(Collection + from);
                await using var file = File.Create(to + ".part");
                await download.CopyToAsync(file);
                file.Close();
                File.Move(to + ".part", to, overwrite: true);
            }
            return model;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            logger.LogWarning("Downloading the Piper voice {Voice} failed: {Message}", voice, ex.Message);
            return null;
        }
        finally
        {
            _downloading.Release();
        }
    }

    private async Task<Dictionary<string, string>> IndexAsync()
    {
        if (_index is { } known)
            return known;
        try
        {
            using var json = JsonDocument.Parse(await Http.GetStringAsync(Collection + "voices.json"));
            return _index = json.RootElement.EnumerateObject()
                .Where(v => v.Name.StartsWith("en_"))
                .ToDictionary(v => v.Name, v => v.Value.GetProperty("files").EnumerateObject().First(f => f.Name.EndsWith(".onnx")).Name);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("Listing Piper voices failed: {Message}", ex.Message);
            return [];
        }
    }
}
