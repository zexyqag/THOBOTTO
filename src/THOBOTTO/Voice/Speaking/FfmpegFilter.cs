using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using THOBOTTO.Helpers;

namespace THOBOTTO.Voice.Speaking;

// A personality's filter chain run by the ffmpeg in the image (the one the watch module has): the line's samples in
// through a pipe, the filtered ones out. A chain that goes on and on (a pad, a bad echo) is cut at a limit.
public sealed partial class FfmpegFilter(IConfiguration config, ILogger<FfmpegFilter> logger) : IVoiceFilter
{
    private static readonly TimeSpan MostTime = TimeSpan.FromSeconds(20);
    private const int MostSeconds = 60;

    // A moment of tone, enough for ffmpeg to say whether a chain runs.
    private static readonly Spoken Probe = new([.. Enumerable.Range(0, 11025).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 22050)))], 22050);

    private string Program => config["Watch:Ffmpeg"] ?? "ffmpeg";

    // The sound through the chain; null, with the reason logged, when ffmpeg can't run it.
    public async Task<Spoken?> ApplyAsync(Spoken sound, string chain, CancellationToken ct)
    {
        var (samples, problem) = await RunAsync(sound, chain, ct);
        if (problem is not null)
            logger.LogWarning("The filters {Chain} didn't run: {Problem}", chain, problem);
        return samples is null ? null : new(samples, sound.Rate);
    }

    public async Task<string?> CheckAsync(string chain, CancellationToken ct = default) => (await RunAsync(Probe, chain, ct)).Problem;

    private async Task<(short[]? Samples, string? Problem)> RunAsync(Spoken sound, string chain, CancellationToken ct)
    {
        var rate = sound.Rate.ToString(CultureInfo.InvariantCulture);
        var start = new ProcessStartInfo(Program, ["-nostdin", "-loglevel", "error", "-f", "s16le", "-ar", rate, "-ac", "1", "-i", "pipe:0",
            "-af", VoiceFilters.WithRate(chain, sound.Rate), "-f", "s16le", "-ar", rate, "-ac", "1", "pipe:1"])
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(MostTime);
        try
        {
            using var ffmpeg = Process.Start(start)!;
            var feeding = FeedAsync(ffmpeg, MemoryMarshal.AsBytes(sound.Samples.AsSpan()).ToArray(), timer.Token);
            var errors = ffmpeg.StandardError.ReadToEndAsync(timer.Token);
            using var output = new MemoryStream();
            var most = MostSeconds * sound.Rate * 2;
            var buffer = new byte[16384];
            int read;
            while ((read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(buffer, timer.Token)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > most)
                {
                    ffmpeg.Kill();
                    return (null, $"The filters go on for more than {MostSeconds} seconds.");
                }
            }
            await feeding;
            await ffmpeg.WaitForExitAsync(timer.Token);
            if (ffmpeg.ExitCode != 0)
                return (null, Reason(await errors));
            return (Pcm.FromBytes(output.ToArray()), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"The filters took more than {MostTime.TotalSeconds} seconds.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            return (null, $"ffmpeg couldn't run: {ex.Message}");
        }
    }

    // ffmpeg stops reading when the chain is bad; that's told by its exit, not by the pipe.
    private static async Task FeedAsync(Process ffmpeg, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await ffmpeg.StandardInput.BaseStream.WriteAsync(bytes, ct);
            ffmpeg.StandardInput.Close();
        }
        catch (IOException)
        {
        }
    }

    // ffmpeg's first complaint, without the addresses it tags lines with.
    private static string Reason(string errors)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => Tag().Replace(l, "").Trim()).Where(l => l.Length > 0).ToList();
        var reason = lines.FirstOrDefault(l => !l.StartsWith("Error ") && !l.StartsWith("Failed ") && !l.StartsWith("Conversion failed")) ?? lines.FirstOrDefault() ?? "ffmpeg refused the filters.";
        return reason.Length > 200 ? reason[..200] : reason;
    }

    [GeneratedRegex(@"\[[^\]]*@ 0x[0-9a-f]+\]\s*")]
    private static partial Regex Tag();
}
