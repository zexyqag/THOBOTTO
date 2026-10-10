using System.Diagnostics;
using System.Text.Json;

namespace THOBOTTO.Watch;

// A video as yt-dlp finds it: what to show, and the streams to play (video and audio, or one with both).
public sealed record Video(string Id, string Title, string? Uploader, string? Thumbnail, double? Seconds, string Page, IReadOnlyList<VideoStream> Streams);

public sealed record VideoStream(string Url, IReadOnlyDictionary<string, string> Headers);

// Finds a video (a link, or search words on YouTube) with yt-dlp, in a form every browser plays: H.264 and AAC.
public sealed class VideoResolver(IConfiguration config, ILogger<VideoResolver> logger)
{
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(1);

    private string Program => config["Watch:YtDlp"] ?? "yt-dlp";

    public async Task<(Video? Video, string? Problem)> ResolveAsync(string query, int height, CancellationToken ct = default)
    {
        var target = Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? query.Trim() : $"ytsearch1:{query.Trim()}";
        var format = $"bv*[height<={height}][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<={height}][vcodec^=avc1][acodec^=mp4a]/b[height<={height}]/b";
        var start = new ProcessStartInfo(Program, ["-J", "--no-playlist", "--no-warnings", "-f", format, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Longest);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var error = (await errors).Split('\n').FirstOrDefault(l => l.StartsWith("ERROR")) ?? "it didn't say why";
                logger.LogWarning("yt-dlp couldn't get {Query}: {Error}", query, error);
                return (null, error.Contains("Sign in") ? "YouTube wants a signed-in account for that one." : $"Couldn't get that video ({error.Replace("ERROR: ", "")}).");
            }
            using var json = JsonDocument.Parse(await output);
            var root = json.RootElement;
            if (root.TryGetProperty("entries", out var entries))
            {
                if (entries.GetArrayLength() == 0)
                    return (null, "Nothing found.");
                root = entries[0];
            }
            var streams = root.TryGetProperty("requested_formats", out var parts) && parts.ValueKind == JsonValueKind.Array
                ? parts.EnumerateArray().Select(Stream).ToList()
                : [Stream(root)];
            return (new(
                root.GetProperty("id").GetString()!,
                root.GetProperty("title").GetString() ?? "A video",
                Text(root, "uploader"),
                Text(root, "thumbnail"),
                root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
                Text(root, "webpage_url") ?? target,
                streams), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("yt-dlp failed for {Query}: {Message}", query, ex.Message);
            return (null, "Videos can't be fetched here right now.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Fetching that video took too long.");
        }
    }

    private static VideoStream Stream(JsonElement format) => new(
        format.GetProperty("url").GetString()!,
        format.TryGetProperty("http_headers", out var headers) ? headers.EnumerateObject().ToDictionary(h => h.Name, h => h.Value.GetString() ?? "") : new Dictionary<string, string>());

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
