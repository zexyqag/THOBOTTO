using System.Runtime.InteropServices;

using NetCord.Gateway;

namespace THOBOTTO.Listening;

// `--check-voice`: loads what listening needs (Opus, libsodium, libdave for NetCord; Whisper's own) and runs
// Piper (helper voices), and says whether it's all there, for checking a built image.
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
        try
        {
            Console.WriteLine($"Whisper: {Whisper.net.WhisperFactory.GetRuntimeInfo()?.Split('\n')[0]}");
        }
        catch (Exception ex)
        {
            missing.Add($"whisper ({ex.Message})");
        }
        foreach (var (name, tool, version) in new[] { ("yt-dlp", "Watch__YtDlp", "--version"), ("ffmpeg", "Watch__Ffmpeg", "-version") })
        {
            if (Environment.GetEnvironmentVariable(tool) is not { } path)
                continue;
            try
            {
                using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, version) { RedirectStandardOutput = true, RedirectStandardError = true })!;
                Console.WriteLine($"{name}: {run.StandardOutput.ReadLine()}");
                run.WaitForExit();
                if (run.ExitCode != 0)
                    missing.Add(name);
            }
            catch (Exception ex)
            {
                missing.Add($"{name} ({ex.Message})");
            }
        }
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
}
