using System.ComponentModel;
using System.Runtime.InteropServices;

using NetCord.Gateway;

namespace THOBOTTO.Voice.Hearing;

// `--check-voice`: loads what listening needs (Opus, libsodium, libdave for NetCord; Whisper's and sherpa-onnx's own) and runs
// Piper (helper voices) and the watching-together tools, and says whether it's all there, for checking a built image.
public static class VoiceCheck
{
    public static int Run()
    {
        var missing = new List<string>();
        foreach (var library in new[] { "opus", "libsodium", "libdave" })
        {
            if (!NativeLibrary.TryLoad(library, typeof(GatewayClient).Assembly, null, out _))
                missing.Add(library);
        }
        // Parakeet's speech to text.
        if (!NativeLibrary.TryLoad("sherpa-onnx-c-api", typeof(SherpaOnnx.OfflineRecognizer).Assembly, null, out _))
            missing.Add("sherpa-onnx");
        try
        {
            Console.WriteLine($"Whisper: {Whisper.net.WhisperFactory.GetRuntimeInfo()?.Split('\n')[0]}");
        }
        catch (Exception ex)
        {
            missing.Add($"whisper ({ex.Message})");
        }
        foreach (var (name, tool, version) in new[] { ("yt-dlp", "Watch__YtDlp", "--version"), ("ffmpeg", "Watch__Ffmpeg", "-version"), ("deno", "Watch__Deno", "--version") })
        {
            if (Environment.GetEnvironmentVariable(tool) is not { } path)
                continue;
            var (said, problem) = Version(path, version);
            Console.WriteLine($"{name}: {said ?? problem}");
            if (problem is not null)
                missing.Add(name);
        }
        if (Environment.GetEnvironmentVariable("Watch__YtDlp") is { } ytDlp && !Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(ytDlp)!, "yt-dlp-plugins"), "*.zip").Any())
            missing.Add("the yt-dlp token helper plugin");
        if (Environment.GetEnvironmentVariable("Speaking__Piper") is { } piper)
        {
            try
            {
                using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(piper, "--help") { RedirectStandardOutput = true, RedirectStandardError = true })!;
                run.WaitForExit();
                Console.WriteLine($"Piper: exit {run.ExitCode}");
            }
            catch (Exception ex)
            {
                missing.Add($"piper ({ex.Message})");
            }
        }
        Console.WriteLine(missing.Count == 0 ? "Voice libraries: all there." : $"Missing: {string.Join(", ", missing)}");
        return missing.Count == 0 ? 0 : 1;
    }

    // A program's first line of output for that argument, or what went wrong.
    public static (string? Version, string? Problem) Version(string path, string argument)
    {
        try
        {
            using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, argument) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var first = run.StandardOutput.ReadLine();
            run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            return run.ExitCode == 0 ? (first, null) : (null, $"exit {run.ExitCode}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }
}
