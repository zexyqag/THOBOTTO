using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using THOBOTTO.GameServers;
using THOBOTTO.Integrations;

namespace THOBOTTO.Links;

// A post's video fetched by the bot itself with yt-dlp, for when no service previews it: as a file to post, or null
// (no video in it, too big, or the site wants signing in and has no cookies here).
public sealed class LinkVideos(IConfiguration config, IntegrationStore store, TimeProvider time, ILogger<LinkVideos> logger)
{
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(1);

    private string Program => config["Watch:YtDlp"] ?? "yt-dlp";

    // When it last fetched one, and when it last couldn't, for the settings page.
    public (DateTimeOffset? Fetched, DateTimeOffset? Failed) Health { get; private set; }

    public async Task<(string Name, byte[] Bytes)?> FetchAsync(string link, long maxBytes)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !await PublicAddress.IsPublicAsync(uri.IdnHost, CancellationToken.None))
            return null;
        var folder = Directory.CreateTempSubdirectory("thobotto-link-");
        try
        {
            var megabytes = maxBytes / 1024 / 1024;
            List<string> arguments = ["--no-playlist", "--no-warnings", "--max-filesize", $"{megabytes}M",
                "-f", $"b[ext=mp4][filesize<?{megabytes}M]/b[filesize<?{megabytes}M]", "-o", Path.Combine(folder.FullName, "video.%(ext)s")];
            if (store.Get(IntegrationStore.LinkCookies) is { } cookies)
            {
                var file = Path.Combine(folder.FullName, "cookies.txt");
                await File.WriteAllTextAsync(file, cookies);
                arguments.AddRange(["--cookies", file]);
            }
            var start = new ProcessStartInfo(Program, [.. arguments, link]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(Longest);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var video = folder.GetFiles("video.*").FirstOrDefault();
            if (process.ExitCode != 0 || video is null || video.Length > maxBytes)
            {
                logger.LogInformation("The bot couldn't fetch the video of {Link}: {Error}", link, (await errors).Split('\n').FirstOrDefault(l => l.StartsWith("ERROR")) ?? "too big, or none");
                Health = (Health.Fetched, time.GetUtcNow());
                return null;
            }
            Health = (time.GetUtcNow(), Health.Failed);
            return (video.Name, await File.ReadAllBytesAsync(video.FullName));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Fetching the video of {Link} failed: {Message}", link, ex.Message);
            Health = (Health.Fetched, time.GetUtcNow());
            return null;
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}

// Cookies of signed-in accounts on sites yt-dlp can't fetch from signed out (Instagram), for the bot's own videos:
// a cookies.txt (several sites at once), or a browser's "Copy as cURL" for one of them.
public static partial class LinkCookies
{
    public static (string? File, string? Problem) Read(string pasted)
    {
        var text = pasted.Trim();
        if (text.Length == 0)
            return (null, "Nothing was pasted.");
        if (text.Contains('\t'))
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Split('\t').Length == 7 && (!l.StartsWith('#') || l.StartsWith("#HttpOnly_"))).ToList();
            return lines.Count == 0 ? (null, "No cookies in that cookies.txt.") : ("# Netscape HTTP Cookie File\n" + string.Join('\n', lines) + "\n", null);
        }
        if (CurlUrl().Match(text) is not { Success: true } url || !Uri.TryCreate(url.Groups["u"].Value, UriKind.Absolute, out var site))
            return (null, "Paste a cookies.txt, or a whole “Copy as cURL” from the site.");
        var unescaped = text.Replace("^\"", "\"").Replace("^%^", "%").Replace("^&", "&").Replace("^", "");
        if (CurlCookies().Match(unescaped) is not { Success: true } header)
            return (null, "That “Copy as cURL” has no cookies: sign in on the site first.");
        var domain = "." + Regex.Replace(site.Host, @"^www\.", "");
        var expires = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        var file = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var pair in header.Groups["v"].Value.Split(';').Select(p => p.Trim()).Where(p => p.Contains('=')))
            file.Append($"{domain}\tTRUE\t/\tTRUE\t{expires}\t{pair[..pair.IndexOf('=')]}\t{pair[(pair.IndexOf('=') + 1)..]}\n");
        return (file.ToString(), null);
    }

    // The sites a cookies file is for, to say which are set.
    public static IReadOnlyList<string> Sites(string file)
        => [.. file.Split('\n').Select(l => l.Split('\t')).Where(f => f.Length == 7).Select(f => f[0].Replace("#HttpOnly_", "").TrimStart('.')).Distinct()];

    [GeneratedRegex(@"curl\s+\^?[""']?(?<u>https?://[^""'\s^]+)")]
    private static partial Regex CurlUrl();

    [GeneratedRegex(@"(?:-b|--cookie|-H\s+[""']?[Cc]ookie:)\s*[""']?(?<v>[^""']+)")]
    private static partial Regex CurlCookies();
}
