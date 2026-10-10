using System.Net.Http.Json;
using System.Text.Json;

namespace THOBOTTO.Integrations;

// Signs the YouTube plugin in from the panel: Google's device sign-in (a code to enter on google.com/device),
// as YouTube on a TV does, with the same public TV client the plugin uses. The refresh token it yields is stored
// and handed to Lavalink. Use a throwaway Google account: YouTube may flag accounts used like this.
public sealed class YoutubeSignIn(IntegrationStore store, TimeProvider time, ILogger<YoutubeSignIn> logger)
{
    private const string ClientId = "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com";
    private const string ClientSecret = "SboVhoG9s0rNafixCSGGKXAT";
    private const string Scopes = "http://gdata.youtube.com https://www.googleapis.com/auth/youtube";
    private static readonly HttpClient Http = new() { BaseAddress = new("https://www.youtube.com/o/oauth2/"), Timeout = TimeSpan.FromSeconds(15) };

    private CancellationTokenSource? _polling;

    public sealed record Pending(string UserCode, string Url, DateTimeOffset Expires);

    // A code waiting to be entered, if any.
    public Pending? Waiting { get; private set; }

    // How the last attempt ended, for the panel.
    public string? Outcome { get; private set; }

    public async Task StartAsync(ulong actorId)
    {
        Cancel();
        using var response = await Http.PostAsJsonAsync("device/code", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scopes,
            ["device_id"] = Guid.NewGuid().ToString("N"),
            ["device_model"] = "ytlr::",
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var expires = time.GetUtcNow().AddSeconds(root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 1800);
        var interval = TimeSpan.FromSeconds(root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 5);
        Waiting = new(root.GetProperty("user_code").GetString()!, root.GetProperty("verification_url").GetString()!, expires);
        Outcome = null;

        // Read before the reply is disposed: the poll runs after this returns.
        var deviceCode = root.GetProperty("device_code").GetString()!;
        var polling = _polling = new CancellationTokenSource();
        _ = Task.Run(() => PollAsync(deviceCode, interval, expires, actorId, polling.Token));
    }

    public void Cancel()
    {
        _polling?.Cancel();
        _polling = null;
        Waiting = null;
    }

    private async Task PollAsync(string deviceCode, TimeSpan interval, DateTimeOffset expires, ulong actorId, CancellationToken ct)
    {
        try
        {
            while (time.GetUtcNow() < expires)
            {
                await Task.Delay(interval, time, ct);
                using var response = await Http.PostAsJsonAsync("token", new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["code"] = deviceCode,
                    ["grant_type"] = "http://oauth.net/grant_type/device/1.0",
                }, ct);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = json.RootElement;
                switch (root.TryGetProperty("error", out var error) ? error.GetString() : null)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case null when root.TryGetProperty("refresh_token", out var token):
                        await store.SetAsync(IntegrationStore.YoutubeRefreshToken, token.GetString(), actorId);
                        Outcome = "Signed in.";
                        return;
                    case var other:
                        Outcome = other == "access_denied" ? "The sign-in was turned down." : $"Google said: {other}.";
                        return;
                }
            }
            Outcome = "The code expired before it was entered.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("YouTube sign-in failed: {Message}", ex.Message);
            Outcome = "Reaching Google failed; try again.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                Waiting = null;
        }
    }
}
