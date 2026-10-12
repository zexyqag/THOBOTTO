using System.Text;
using System.Text.RegularExpressions;

using THOBOTTO.Discord;
using THOBOTTO.Integrations;

namespace THOBOTTO.Activity.Watch;

// A YouTube account's cookies for yt-dlp, for when YouTube won't hand over a video without signing in. Pasted on the
// Integrations page (a cookies.txt, a browser's "Copy as cURL", or just the Cookie header), checked to be signed in,
// kept up to date as yt-dlp refreshes them, and checked daily: the owner gets a DM when they stop working.
public sealed partial class YoutubeCookies(IntegrationStore store, BotOwners owners, [FromKeyedServices(nameof(YoutubeCookies))] HttpClient http, TimeProvider time, ILogger<YoutubeCookies> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);
    // What tells YouTube it's a signed-in account.
    private static readonly string[] SignedIn = ["SAPISID", "__Secure-3PAPISID", "SID", "__Secure-3PSID", "LOGIN_INFO"];

    public bool Set => store.Get(IntegrationStore.WatchCookies) is not null;

    // Whether they worked when last checked: null before the first check.
    public bool? Working { get; private set; }

    // Reads what was pasted into a cookies.txt for YouTube; the problem when it isn't one.
    public static (string? Cookies, string? Problem) Read(string pasted)
    {
        var text = pasted.Trim();
        if (text.Length == 0)
            return (null, "Nothing was pasted.");
        IEnumerable<(string Name, string Value)> pairs;
        if (text.Contains('\t'))
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Split('\t').Length == 7 && !l.StartsWith('#') && Regex.IsMatch(l, @"^(#HttpOnly_)?\.?(youtube|google)\.com"));
            pairs = lines.Select(l => l.Split('\t')).Select(f => (f[5], f[6]));
        }
        else if (PowerShellCookies().IsMatch(text))
            pairs = PowerShellCookies().Matches(text).Select(m => (m.Groups["n"].Value, m.Groups["v"].Value));
        else
        {
            // A cURL command: Chrome's -b '…' (bash) or -b ^"…^" (cmd), Firefox's -H 'Cookie: …'; else the header itself.
            var unescaped = text.Replace("^\"", "\"").Replace("^%^", "%").Replace("^&", "&").Replace("^", "");
            var header = CurlCookies().Match(unescaped) is { Success: true } m ? m.Groups["v"].Value : Regex.Replace(unescaped, @"^cookie:\s*", "", RegexOptions.IgnoreCase);
            pairs = header.Split(';').Select(p => p.Trim()).Where(p => p.Contains('=')).Select(p => (p[..p.IndexOf('=')], p[(p.IndexOf('=') + 1)..]));
        }
        var cookies = pairs.Where(p => p.Name.Length > 0 && !p.Name.Contains(' ')).DistinctBy(p => p.Name).ToList();
        if (cookies.Count == 0)
            return (null, "No cookies found in that: paste the whole “Copy as cURL”, the Cookie header, or a cookies.txt.");
        if (!cookies.Any(c => SignedIn.Contains(c.Name)))
            return (null, "Those cookies aren't from a signed-in YouTube account: sign in first, then copy them from youtube.com.");
        var expires = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        var file = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var (name, value) in cookies)
            file.Append($".youtube.com\tTRUE\t/\tTRUE\t{expires}\t{name}\t{value}\n");
        return (file.ToString(), null);
    }

    // Saves them when YouTube takes them as signed in. The answer for the owner.
    public async Task<(bool Saved, string Answer)> SaveAsync(string pasted, ulong actorId)
    {
        var (cookies, problem) = Read(pasted);
        if (cookies is null)
            return (false, problem!);
        if (await SignedInAsync(cookies) is not true)
            return (false, "YouTube doesn't take those as signed in: they may be from before signing in, or already replaced. Copy them again and close that window right after.");
        await store.SetAsync(IntegrationStore.WatchCookies, cookies, actorId);
        await store.SetAsync(IntegrationStore.WatchCookiesWarned, null, actorId);
        Working = true;
        return (true, "Saved: YouTube takes them as signed in. They're only used when YouTube insists on it.");
    }

    public async Task RemoveAsync(ulong actorId)
    {
        await store.SetAsync(IntegrationStore.WatchCookies, null, actorId);
        Working = null;
    }

    // A file of them for one yt-dlp run; after it, what yt-dlp refreshed is kept and the file goes.
    public async Task<T> WithFileAsync<T>(Func<string, Task<T>> run)
    {
        var path = Path.Combine(Path.GetTempPath(), $"yt-{Guid.NewGuid():N}.txt");
        var cookies = store.Get(IntegrationStore.WatchCookies)!;
        await File.WriteAllTextAsync(path, cookies);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var result = await run(path);
            var after = await File.ReadAllTextAsync(path);
            if (after != cookies && SignedIn.Any(after.Contains))
                await store.SetAsync(IntegrationStore.WatchCookies, after, 0);
            return result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // yt-dlp was refused even with them.
    public Task FailedAsync() => WarnAsync();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        do
        {
            try
            {
                if (store.Get(IntegrationStore.WatchCookies) is { } cookies && await SignedInAsync(cookies) is { } signedIn)
                {
                    Working = signedIn;
                    if (!signedIn)
                        await WarnAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking the YouTube cookies failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Null when YouTube couldn't be asked.
    private async Task<bool?> SignedInAsync(string cookies)
    {
        var header = string.Join("; ", cookies.Split('\n').Select(l => l.Split('\t')).Where(f => f.Length == 7).Select(f => $"{f[5]}={f[6]}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
        request.Headers.Add("Cookie", header);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0");
        try
        {
            using var answer = await http.SendAsync(request);
            return (await answer.Content.ReadAsStringAsync()).Contains("\"LOGGED_IN\":true");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Once, until new ones are saved: the owner (or the team) gets a DM.
    private async Task WarnAsync()
    {
        Working = false;
        if (store.Get(IntegrationStore.WatchCookiesWarned) is not null)
            return;
        await store.SetAsync(IntegrationStore.WatchCookiesWarned, time.GetUtcNow().ToString("O"), 0);
        await owners.DmAsync("🎬 YouTube no longer takes the account cookies used for watching together, so videos that need signing in won't play. Copy them again on the panel's Integrations page.");
    }

    [GeneratedRegex("""(?:-b|--cookie)\s+(?<q>['"])(?<v>.*?)\k<q>|-H\s+(?<q>['"])cookie:\s*(?<v>.*?)\k<q>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CurlCookies();

    // The PowerShell variant: $session.Cookies.Add((New-Object System.Net.Cookie("SID", "…", "/", ".youtube.com"))).
    [GeneratedRegex(""""System\.Net\.Cookie\("(?<n>[^"]+)",\s*"(?<v>[^"]*)"""")]
    private static partial Regex PowerShellCookies();
}
