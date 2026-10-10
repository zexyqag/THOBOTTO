using System.Diagnostics;
using System.Text.Json;

using THOBOTTO.Integrations;

namespace THOBOTTO.Watch;

// A video as yt-dlp finds it: what to show, and the streams to play (video and audio, or one with both).
public sealed record Video(string Id, string Title, string? Uploader, string? Thumbnail, double? Seconds, string Page, IReadOnlyList<VideoStream> Streams);

public sealed record VideoStream(string Url, IReadOnlyDictionary<string, string> Headers);

// Finds a video (a link, or search words on YouTube) with yt-dlp, in a form every browser plays: H.264 and AAC.
// YouTube's checks are answered by the token helper and a JavaScript runtime; when it still insists on signing in,
// it's asked again with the account's cookies, if there are any.
public sealed class VideoResolver(IConfiguration config, IntegrationStore store, YoutubeCookies cookies, ILogger<VideoResolver> logger)
{
    public const string TokenHelperDefault = "http://youtube-pot:4416";
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(1);

    private string Program => config["Watch:YtDlp"] ?? "yt-dlp";

    public string TokenHelper => store.Get(IntegrationStore.WatchTokenHelper) ?? TokenHelperDefault;

    // Deno, for YouTube's challenges: where the image puts it, else wherever yt-dlp finds it.
    public string JavaScript => config["Watch:Deno"] ?? "deno";

    public async Task<(Video? Video, string? Problem)> ResolveAsync(string query, int height, CancellationToken ct = default)
    {
        var target = Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? query.Trim() : $"ytsearch1:{query.Trim()}";
        var format = $"bv*[height<={height}][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<={height}][vcodec^=avc1][acodec^=mp4a]/b[height<={height}]/b";
        List<string> arguments = ["-J", "--no-playlist", "--no-warnings", "-f", format, "--extractor-args", $"youtubepot-bgutilhttp:base_url={TokenHelper}"];
        if (config["Watch:Deno"] is { Length: > 0 } deno)
            arguments.AddRange(["--js-runtimes", $"deno:{deno}"]);
        try
        {
            var (output, error) = await RunAsync([.. arguments, target], ct);
            if (error is not null && SignInWanted(error) && cookies.Set)
            {
                logger.LogInformation("YouTube wants signing in for {Query}; asking again with the account", query);
                (output, error) = await cookies.WithFileAsync(file => RunAsync([.. arguments, "--cookies", file, target], ct));
                if (error is not null && SignInWanted(error))
                    await cookies.FailedAsync();
            }
            if (error is not null)
            {
                logger.LogWarning("yt-dlp couldn't get {Query}: {Error}", query, error);
                return (null, SignInWanted(error)
                    ? cookies.Set ? "YouTube wouldn't hand that one over, even signed in." : "YouTube wants a signed-in account for that one: the bot's owner can add one on the Integrations page."
                    : $"Couldn't get that video ({error.Replace("ERROR: ", "")}).");
            }
            using var json = JsonDocument.Parse(output!);
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

    private static bool SignInWanted(string error) => error.Contains("Sign in") || error.Contains("not a bot");

    // yt-dlp's JSON, or its error.
    private async Task<(string? Output, string? Error)> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Longest);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode == 0 ? (await output, null)
            : (null, (await errors).Split('\n').FirstOrDefault(l => l.StartsWith("ERROR")) ?? "it didn't say why");
    }

    private static VideoStream Stream(JsonElement format) => new(
        format.GetProperty("url").GetString()!,
        format.TryGetProperty("http_headers", out var headers) ? headers.EnumerateObject().ToDictionary(h => h.Name, h => h.Value.GetString() ?? "") : new Dictionary<string, string>());

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
