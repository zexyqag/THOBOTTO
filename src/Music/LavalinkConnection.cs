using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace THOBOTTO.Music;

public sealed record LavalinkEvent(string Type, ulong GuildId, string? Reason, string? Message);

// Where a player is in its track (ms), as of Lavalink's clock (unix ms).
public sealed record LavalinkPosition(ulong GuildId, long Time, long Position);

// One bot account's link to Lavalink: a WebSocket for its session and events, REST for its players.
// Reconnects on its own; players don't survive a reconnect, so their owners set them up again (Ready).
public sealed class LavalinkConnection(LavalinkOptions options, ulong userId, ILogger logger)
{
    private static readonly HttpClient Http = new();

    public string? SessionId { get; private set; }

    public event Func<LavalinkEvent, Task>? Event;

    public event Func<LavalinkPosition, Task>? PlayerUpdate;

    // A new session: Lavalink has no players for it yet.
    public event Func<Task>? Ready;

    public async Task RunAsync(CancellationToken ct)
    {
        var uri = new Uri(new Uri(options.BaseAddress.Replace("http", "ws")), "/v4/websocket");
        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", options.Passphrase);
            socket.Options.SetRequestHeader("User-Id", userId.ToString());
            socket.Options.SetRequestHeader("Client-Name", "THOBOTTO");
            try
            {
                await socket.ConnectAsync(uri, ct);
                await ReceiveAsync(socket, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Lavalink connection for {UserId} dropped: {Message}", userId, ex.Message);
            }

            SessionId = null;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    public static async Task<LoadResult> LoadAsync(LavalinkOptions options, string identifier, ulong requestedBy, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseAddress}/v4/loadtracks?identifier={Uri.EscapeDataString(identifier)}");
        request.Headers.Add("Authorization", options.Passphrase);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        var data = root.GetProperty("data");

        return root.GetProperty("loadType").GetString() switch
        {
            "track" => new([Track.From(data, requestedBy)], null, null),
            "search" => new(data.EnumerateArray().Select(t => Track.From(t, requestedBy)).ToList(), null, null),
            "playlist" => new(data.GetProperty("tracks").EnumerateArray().Select(t => Track.From(t, requestedBy)).ToList(),
                data.GetProperty("info").GetProperty("name").GetString(), null),
            "error" => new([], null, data.GetProperty("message").GetString() ?? "Loading failed."),
            _ => new([], null, null),
        };
    }

    public Task UpdatePlayerAsync(ulong guildId, JsonObject body, bool noReplace = false)
        => SendAsync(HttpMethod.Patch, $"/v4/sessions/{SessionId}/players/{guildId}?noReplace={noReplace.ToString().ToLowerInvariant()}", body);

    public Task DestroyPlayerAsync(ulong guildId) => SendAsync(HttpMethod.Delete, $"/v4/sessions/{SessionId}/players/{guildId}", null);

    private async Task SendAsync(HttpMethod method, string path, JsonObject? body)
    {
        if (SessionId is null)
            throw new InvalidOperationException("Lavalink isn't connected.");

        using var request = new HttpRequestMessage(method, options.BaseAddress + path);
        request.Headers.Add("Authorization", options.Passphrase);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lavalink {method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            await HandleAsync(text);
        }
    }

    private async Task HandleAsync(string text)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        switch (root.GetProperty("op").GetString())
        {
            case "ready":
                SessionId = root.GetProperty("sessionId").GetString();
                logger.LogInformation("Lavalink session {SessionId} for {UserId}", SessionId, userId);
                if (Ready is { } ready)
                    await ready();
                break;
            case "event" when Event is { } handler:
                var e = new LavalinkEvent(
                    root.GetProperty("type").GetString()!,
                    ulong.Parse(root.GetProperty("guildId").GetString()!),
                    root.TryGetProperty("reason", out var reason) ? reason.ToString() : null,
                    root.TryGetProperty("exception", out var ex) && ex.TryGetProperty("message", out var m) ? m.GetString() : null);
                try
                {
                    await handler(e);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Handling Lavalink {Type} failed", e.Type);
                }
                break;
            case "playerUpdate" when PlayerUpdate is { } handler:
                var state = root.GetProperty("state");
                if (!state.TryGetProperty("position", out var position))
                    break;
                try
                {
                    await handler(new(ulong.Parse(root.GetProperty("guildId").GetString()!), state.GetProperty("time").GetInt64(), position.GetInt64()));
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Handling a Lavalink player update failed");
                }
                break;
        }
    }
}
