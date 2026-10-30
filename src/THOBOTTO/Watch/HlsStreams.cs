using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace THOBOTTO.Watch;

// Videos made playable in any browser: ffmpeg copies the video and audio (no re-encoding, so hardly any CPU)
// into HLS segments in a folder of their own, as fast as they download. Served to the Activity; a stream nobody
// has asked for in a while is stopped and its folder removed.
public sealed partial class HlsStreams(IConfiguration config, TimeProvider time, ILogger<HlsStreams> logger) : IDisposable
{
    private const int StreamsPerGuild = 3;
    private static readonly TimeSpan Unused = TimeSpan.FromMinutes(15);

    private sealed class Entry(ulong guildId, Process ffmpeg, string folder, DateTimeOffset now)
    {
        public ulong GuildId => guildId;

        public Process Ffmpeg => ffmpeg;

        public string Folder => folder;

        public DateTimeOffset LastUsed { get; set; } = now;

        // What ffmpeg said when it gave up.
        public string? Problem { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _streams = new();

    private string Program => config["Watch:Ffmpeg"] ?? "ffmpeg";

    private string Root => config["Watch:Directory"] is { Length: > 0 } configured ? configured : Path.Combine(Path.GetTempPath(), "thobotto-watch");

    public int Running => _streams.Values.Count(s => !s.Ffmpeg.HasExited);

    // The folders an earlier run left behind (it crashed, or was killed): their streams went with it.
    public void RemoveLeftovers()
    {
        if (!Directory.Exists(Root))
            return;
        try
        {
            foreach (var folder in Directory.GetDirectories(Root))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Removing an earlier run's stream folders failed: {Message}", ex.Message);
        }
    }

    private int RunningIn(ulong guildId) => _streams.Values.Count(s => s.GuildId == guildId && !s.Ffmpeg.HasExited);

    // Starts one; its id (in the files' address), or null when ffmpeg can't run or the server has its share running.
    public string? Start(ulong guildId, Video video)
    {
        if (RunningIn(guildId) >= StreamsPerGuild)
        {
            logger.LogWarning("Not streaming {Title}: {GuildId} has {Count} streams running already", video.Title, guildId, StreamsPerGuild);
            return null;
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo(Program) { WorkingDirectory = folder, RedirectStandardError = true };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "error" })
            start.ArgumentList.Add(arg);
        foreach (var stream in video.Streams)
        {
            if (stream.Headers.Count > 0)
            {
                start.ArgumentList.Add("-headers");
                start.ArgumentList.Add(string.Concat(stream.Headers.Select(h => $"{h.Key}: {h.Value}\r\n")));
            }
            foreach (var arg in new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5", "-i", stream.Url })
                start.ArgumentList.Add(arg);
        }
        string[] mapping = video.Streams.Count > 1 ? ["-map", "0:v:0", "-map", "1:a:0"] : ["-map", "0:v:0?", "-map", "0:a:0?"];
        foreach (var arg in mapping.Concat(["-c", "copy", "-f", "hls", "-hls_time", "4", "-hls_list_size", "0", "-hls_playlist_type", "event",
                     "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", "seg%05d.m4s", "index.m3u8"]))
            start.ArgumentList.Add(arg);
        try
        {
            var ffmpeg = Process.Start(start)!;
            var entry = _streams[id] = new(guildId, ffmpeg, folder, time.GetUtcNow());
            logger.LogDebug("Streaming {Title} as {Id} for {GuildId} ({Parts} input(s))", video.Title, id, guildId, video.Streams.Count);
            _ = LogErrorsAsync(entry, video.Title);
            return id;
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning("ffmpeg couldn't start for {GuildId}: {Message}", guildId, ex.Message);
            return null;
        }
    }

    private static readonly TimeSpan FirstFiles = TimeSpan.FromSeconds(8);

    // A file of a stream (its playlist, init or a segment), or null. Just after the start, it waits for ffmpeg to
    // write the first ones.
    public async Task<string?> FileOfAsync(string id, string name, CancellationToken ct)
    {
        if (!_streams.TryGetValue(id, out var entry) || !FileName().IsMatch(name))
            return null;
        entry.LastUsed = time.GetUtcNow();
        var path = Path.Combine(entry.Folder, name);
        var waited = TimeSpan.Zero;
        while (!File.Exists(path) && waited < FirstFiles && !entry.Ffmpeg.HasExited)
        {
            await Task.Delay(250, ct);
            waited += TimeSpan.FromMilliseconds(250);
        }
        if (File.Exists(path))
            return path;
        logger.LogDebug("Stream {Id}: {File} isn't there ({Problem})", id, name, Failed(id) ?? "not written yet, or the stream ended");
        return null;
    }

    public void Stop(string id)
    {
        if (!_streams.TryRemove(id, out var entry))
            return;
        try
        {
            if (!entry.Ffmpeg.HasExited)
                entry.Ffmpeg.Kill();
            entry.Ffmpeg.Dispose();
            Directory.Delete(entry.Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogDebug("Cleaning up stream {Id} of {GuildId}: {Message}", id, entry.GuildId, ex.Message);
        }
    }

    // Streams nobody has fetched from in a while, unless still in use.
    public void Sweep(IReadOnlySet<string> inUse)
    {
        foreach (var (id, entry) in _streams.Where(s => !inUse.Contains(s.Key) && time.GetUtcNow() - s.Value.LastUsed > Unused).ToList())
            Stop(id);
    }

    private async Task LogErrorsAsync(Entry entry, string title)
    {
        var errors = await entry.Ffmpeg.StandardError.ReadToEndAsync();
        if (errors.Length > 0)
        {
            entry.Problem = errors.Split('\n')[0].Trim();
            logger.LogWarning("ffmpeg for {Title} in {GuildId}: {Errors}", title, entry.GuildId, entry.Problem);
        }
    }

    // Why a stream stopped before its end, or null while it's fine (or ended well).
    // How much of a stream is made so far, and whether that's all of it.
    public (TimeSpan Made, bool Whole) Made(string id)
    {
        if (!_streams.TryGetValue(id, out var entry))
            return (TimeSpan.Zero, false);
        try
        {
            var (seconds, whole) = Playlist(File.ReadAllText(Path.Combine(entry.Folder, "index.m3u8")));
            return (TimeSpan.FromSeconds(seconds), whole || entry.Ffmpeg.HasExited);
        }
        catch (IOException)
        {
            return (TimeSpan.Zero, entry.Ffmpeg.HasExited);
        }
    }

    // The seconds of the segments a playlist lists (each listed once it's written), and whether it's complete.
    public static (double Seconds, bool Whole) Playlist(string text)
        => (Segment().Matches(text).Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)), text.Contains("#EXT-X-ENDLIST"));

    public string? Failed(string id)
        => _streams.TryGetValue(id, out var entry) && entry.Ffmpeg.HasExited && entry.Ffmpeg.ExitCode != 0 ? entry.Problem ?? $"ffmpeg stopped ({entry.Ffmpeg.ExitCode})" : null;

    public void Dispose()
    {
        foreach (var id in _streams.Keys.ToList())
            Stop(id);
    }

    [GeneratedRegex(@"^(index\.m3u8|init\.mp4|seg\d{5}\.m4s)$")]
    private static partial Regex FileName();

    [GeneratedRegex(@"#EXTINF:([\d.]+)")]
    private static partial Regex Segment();
}
