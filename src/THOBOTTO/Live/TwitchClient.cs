using System.Text.RegularExpressions;

using THOBOTTO.Integrations;

using TwitchLib.Api;

namespace THOBOTTO.Live;

public sealed record TwitchUser(string Id, string Login, string Name);

public sealed record TwitchLive(string Id, string Login, string Name, string? Title, string? Game, DateTimeOffset StartedAt);

// Twitch's API through TwitchLib, with the application's id and secret from the Integrations page (an app token: no
// one's Twitch login). TwitchLib gets no logger: it logs each request's address and answer, and the token's request
// carries the secret in its address.
public sealed partial class TwitchClient(IntegrationStore store)
{
    // At most this many channels in one ask.
    private const int Batch = 100;

    private (string Settings, TwitchAPI Api)? _api;

    public bool Configured => store.Get(IntegrationStore.TwitchClientId) is not null && store.Get(IntegrationStore.TwitchClientSecret) is not null;

    private async Task<TwitchAPI> ApiAsync()
    {
        var (id, secret) = (store.Get(IntegrationStore.TwitchClientId)!, store.Get(IntegrationStore.TwitchClientSecret)!);
        if (_api is { } known && known.Settings == $"{id}|{secret}")
            return known.Api;
        var api = new TwitchAPI();
        (api.Settings.ClientId, api.Settings.Secret) = (id, secret);
        api.Settings.AccessToken = await api.Auth.GetAccessTokenAsync();
        _api = ($"{id}|{secret}", api);
        return api;
    }

    // Asked again with a new app token when the old one has run out.
    private async Task<T> AskAsync<T>(Func<TwitchAPI, Task<T>> ask)
    {
        try
        {
            return await ask(await ApiAsync());
        }
        catch (TwitchLib.Api.Core.Exceptions.TokenExpiredException)
        {
            _api = null;
            return await ask(await ApiAsync());
        }
    }

    // The channel a name or link is, or null for none.
    public async Task<TwitchUser?> FindAsync(string channel)
    {
        if (Login(channel) is not { } login)
            return null;
        var users = await AskAsync(api => api.Helix.Users.GetUsersAsync(logins: [login]));
        return users.Users.FirstOrDefault() is { } user ? new(user.Id, user.Login, user.DisplayName) : null;
    }

    // Which of these are live now.
    public async Task<IReadOnlyDictionary<string, TwitchLive>> LiveAsync(IReadOnlyCollection<string> ids)
    {
        var live = new Dictionary<string, TwitchLive>();
        foreach (var batch in ids.Chunk(Batch))
        {
            var streams = await AskAsync(api => api.Helix.Streams.GetStreamsAsync(first: Batch, userIds: [.. batch]));
            foreach (var stream in streams.Streams.Where(s => s.Type == "live"))
                live[stream.UserId] = new(stream.UserId, stream.UserLogin, stream.UserName, Blank(stream.Title), Blank(stream.GameName), Utc(stream.StartedAt));
        }
        return live;
    }

    // Null when the id and secret work, else why not.
    public async Task<string?> ProblemAsync()
    {
        try
        {
            _api = null;
            return await FindAsync("twitch") is null ? "Twitch's own channel couldn't be found." : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    public static string Url(string login) => $"https://www.twitch.tv/{login}";

    // A channel's login from what people type: the name, @name, or its link.
    public static string? Login(string channel)
        => ChannelName().Match(channel.Trim()) is { Success: true } match ? match.Groups["login"].Value.ToLowerInvariant() : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    // Twitch's times are UTC, however the JSON reader marked them.
    private static DateTimeOffset Utc(DateTime at) => at.Kind switch
    {
        DateTimeKind.Local => new(at.ToUniversalTime()),
        _ => new(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
    };

    [GeneratedRegex(@"^(?:(?:https?://)?(?:www\.|m\.)?twitch\.tv/|@)?(?<login>[A-Za-z0-9_]{3,25})/?$")]
    private static partial Regex ChannelName();
}
