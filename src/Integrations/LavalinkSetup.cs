using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using THOBOTTO.Helpers;
using THOBOTTO.Music;

namespace THOBOTTO.Integrations;

// Settings Lavalink takes while running: the YouTube sign-in, and Spotify's. Handed over whenever Lavalink
// starts (it forgets them on a restart) and whenever the owner changes them. A refused token only warns,
// where in Lavalink's own settings it would stop Lavalink from starting at all.
public sealed class LavalinkSetup : IHelperAware
{
    private readonly IntegrationStore _store;
    private readonly LavalinkOptions _lavalink;
    private readonly TimeProvider _time;
    private readonly ILogger<LavalinkSetup> _logger;
    private DateTimeOffset _appliedAt = DateTimeOffset.MinValue;

    public LavalinkSetup(IntegrationStore store, IOptions<LavalinkOptions> lavalink, TimeProvider time, ILogger<LavalinkSetup> logger)
    {
        (_store, _lavalink, _time, _logger) = (store, lavalink.Value, time, logger);
        _store.Changed += OnChangedAsync;
    }

    // How the last YouTube sign-in went: true accepted, false refused, null not tried.
    public bool? YoutubeSignedIn { get; private set; }

    public Task AttachAsync(HelperBot helper)
    {
        helper.Lavalink.Ready += OnLavalinkReadyAsync;
        return Task.CompletedTask;
    }

    public Task DetachAsync(HelperBot helper)
    {
        helper.Lavalink.Ready -= OnLavalinkReadyAsync;
        return Task.CompletedTask;
    }

    // Every helper's link reports Lavalink starting; once per start is enough.
    private async Task OnLavalinkReadyAsync()
    {
        if (_time.GetUtcNow() - _appliedAt < TimeSpan.FromMinutes(1))
            return;
        _appliedAt = _time.GetUtcNow();
        await SignInYoutubeAsync();
        await SendSpotifyAsync();
    }

    private Task OnChangedAsync(string key) => key switch
    {
        IntegrationStore.YoutubeRefreshToken => SignInYoutubeAsync(),
        IntegrationStore.SpotifyClientId or IntegrationStore.SpotifyClientSecret or IntegrationStore.SpotifyAlbumsAndPlaylists => SendSpotifyAsync(),
        _ => Task.CompletedTask,
    };

    private async Task SignInYoutubeAsync()
    {
        if (_store.Get(IntegrationStore.YoutubeRefreshToken) is not { Length: > 0 } token)
            return;
        try
        {
            YoutubeSignedIn = await LavalinkConnection.SignInYoutubeAsync(_lavalink, token);
            if (YoutubeSignedIn == true)
                _logger.LogInformation("Signed Lavalink's YouTube plugin in");
            else
                _logger.LogWarning("YouTube refused the refresh token, so YouTube plays signed out; sign in again on the panel's Integrations page");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Signing Lavalink's YouTube plugin in failed: {Message}", ex.Message);
        }
    }

    // Only what the panel set; the rest stays as Lavalink's own settings have it.
    private async Task SendSpotifyAsync()
    {
        var spotify = new JsonObject();
        if (_store.Get(IntegrationStore.SpotifyClientId) is { } id)
            spotify["clientId"] = id;
        if (_store.Get(IntegrationStore.SpotifyClientSecret) is { } secret)
            spotify["clientSecret"] = secret;
        if (_store.Get(IntegrationStore.SpotifyAlbumsAndPlaylists) is { } mode)
            spotify["preferPartnerApi"] = mode == "true";
        if (spotify.Count == 0)
            return;
        try
        {
            if (!await LavalinkConnection.ConfigureLavaSrcAsync(_lavalink, new JsonObject { ["spotify"] = spotify }))
                _logger.LogWarning("Lavalink didn't take the Spotify settings (is LavaSrc installed?)");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Sending Spotify settings to Lavalink failed: {Message}", ex.Message);
        }
    }
}
