using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

using THOBOTTO.Helpers;

namespace THOBOTTO.Voice.Speaking;

// eSpeak NG inside the image: the vintage robot voice, small and instant. Voices as it lists them ("en-us",
// "en-gb-x-rp"), a variant after a plus ("en-us+m3", "en-gb+whisper").
public sealed class EspeakVoices(IConfiguration config, ILogger<EspeakVoices> logger)
{
    private const int WordsPerMinute = 175;

    // The variants in its data (voices/!v), after a plus: male and female ones, and a few characters.
    public static readonly IReadOnlyList<string> Variants = ["m1", "m2", "m3", "m4", "m5", "m6", "m7", "f1", "f2", "f3", "f4", "f5", "croak", "whisper", "klatt", "klatt2", "klatt3"];
    private string Program => config["Speaking:Espeak"] ?? "espeak-ng";
    private IReadOnlyList<string>? _voices;

    public async Task<IReadOnlyList<string>> VoicesAsync()
    {
        if (_voices is not null)
            return _voices;
        try
        {
            using var run = Process.Start(Start(Program, ["--voices"]))!;
            var listing = await run.StandardOutput.ReadToEndAsync();
            await run.WaitForExitAsync();
            return _voices = ParseVoices(listing);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            logger.LogWarning("Listing eSpeak NG's voices failed: {Message}", ex.Message);
            return [];
        }
    }

    // The language column of `espeak-ng --voices`, each once.
    public static IReadOnlyList<string> ParseVoices(string listing)
        => listing.Split('\n').Skip(1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(c => c.Length > 2)
            .Select(c => c[1])
            .Distinct()
            .Order()
            .ToList();

    public async Task<Spoken?> SayAsync(string text, string voice, double speed, CancellationToken ct)
    {
        var wordsPerMinute = Math.Clamp((int)(WordsPerMinute * speed), 80, 450);
        try
        {
            using var espeak = Process.Start(Start(Program, ["--stdin", "--stdout", "-v", voice, "-s", wordsPerMinute.ToString(CultureInfo.InvariantCulture)]))!;
            await espeak.StandardInput.WriteLineAsync(text.ReplaceLineEndings(" "));
            espeak.StandardInput.Close();
            using var audio = new MemoryStream();
            await espeak.StandardOutput.BaseStream.CopyToAsync(audio, ct);
            await espeak.WaitForExitAsync(ct);
            if (espeak.ExitCode != 0)
            {
                logger.LogWarning("eSpeak NG failed: {Error}", await espeak.StandardError.ReadToEndAsync(ct));
                return null;
            }
            return Pcm.FromWav(audio.ToArray()) is { } wav ? new(wav.Samples, wav.Rate) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            logger.LogWarning("Running eSpeak NG failed: {Message}", ex.Message);
            return null;
        }
    }

    // In the image the program sits with its data and the libraries it links, apart from the system's.
    public static ProcessStartInfo Start(string program, string[] args)
    {
        var start = new ProcessStartInfo(program, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (program.Contains('/'))
        {
            var folder = Path.GetDirectoryName(program)!;
            if (Directory.Exists(Path.Combine(folder, "lib")))
                start.Environment["LD_LIBRARY_PATH"] = Path.Combine(folder, "lib");
            if (Directory.Exists(Path.Combine(folder, "espeak-ng-data")))
                start.Environment["ESPEAK_DATA_PATH"] = Path.Combine(folder, "espeak-ng-data");
        }
        return start;
    }
}
