using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Debugging;

namespace THOBOTTO.GameServers;

// A bridged server as the bridge keeps it.
public sealed record Bridged(long ServerId, ulong ChannelId, string Name, string Game, ulong? WebhookId, bool Into);

// Discord channels and game servers talking together. A member's message in a bridge's channel is said in its games;
// what a game says comes into the channel through its webhook (a chat add-on posting there, or the bot posting the chat
// its console streams: Rust's, 7 Days to Die's), and from there is said in the bridge's other games. What the bridge
// says starts with [Discord] or [<server>], and isn't passed on again.
public sealed partial class ChatBridge(
    IDbContextFactory<BotDbContext> dbFactory,
    ServerControl control,
    ServerDetails details,
    ServerWatch watch,
    RestClient rest,
    GatewayClient gateway,
    ILogger<ChatBridge> logger) : BackgroundService
{
    private const int Longest = 200;
    private static readonly TimeSpan Reload = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Reconnect = TimeSpan.FromSeconds(30);

    // Said in order, so a burst of messages doesn't open a connection to each game for each at once; past a backlog
    // the oldest are dropped.
    private readonly Channel<Func<Task>> _work = System.Threading.Channels.Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile IReadOnlyDictionary<ulong, IReadOnlyList<Bridged>> _bridges = new Dictionary<ulong, IReadOnlyList<Bridged>>();
    // The consoles listened to for chat, by server, with what they were started with: a change starts them again.
    private readonly ConcurrentDictionary<long, (string Started, CancellationTokenSource Stop)> _listening = new();

    // Whether its console streams the players' chat, so no add-on is needed for it.
    public static bool Streams(string game) => GameControls.Console(game) is RemoteConsole.WebRcon or RemoteConsole.Telnet;

    public void Seen(Message message)
    {
        if (!_bridges.TryGetValue(message.ChannelId, out var servers) || Plain(message) is not { Length: > 0 } text)
            return;
        if (message.WebhookId is { } hook)
        {
            if (servers.FirstOrDefault(s => s.WebhookId == hook) is { } from && !Ours(text, servers.Select(s => s.Name)))
                Queue([.. servers.Where(s => s != from)], $"[{from.Name}] {Player(message.Author.Username, from.Name)}: {text}");
        }
        else if (!message.Author.IsBot)
            Queue([.. servers.Where(s => s.Into)], $"[Discord] {(message.Author as GuildUser)?.Nickname ?? message.Author.GlobalName ?? message.Author.Username}: {text}");
    }

    // A line the bridge itself said, come back.
    public static bool Ours(string text, IEnumerable<string> servers) => text.StartsWith("[Discord]", StringComparison.Ordinal) || servers.Any(s => text.StartsWith($"[{s}]", StringComparison.Ordinal));

    // The player a webhook speaks as, without the server the bot adds to the names it posts with.
    public static string Player(string username, string server) => username.EndsWith($" · {server}", StringComparison.Ordinal) ? username[..^(server.Length + 3)] : username;

    // A message as a game can show it: mentions by name, emojis by theirs, files said, one short line.
    private string Plain(Message message)
    {
        gateway.Cache.Guilds.TryGetValue(message.GuildId ?? 0, out var guild);
        var text = Mention().Replace(message.Content, m => m.Groups["kind"].Value switch
        {
            "@&" => guild is not null && guild.Roles.TryGetValue(ulong.Parse(m.Groups["id"].Value), out var role) ? $"@{role.Name}" : "@role",
            "#" => guild is not null && guild.Channels.TryGetValue(ulong.Parse(m.Groups["id"].Value), out var channel) ? $"#{channel.Name}" : "#channel",
            _ => guild is not null && guild.Users.TryGetValue(ulong.Parse(m.Groups["id"].Value), out var user) ? $"@{user.Nickname ?? user.GlobalName ?? user.Username}" : "@someone",
        });
        text = Emoji().Replace(text, ":${name}:");
        if (message.Attachments.Count > 0)
            text += message.Attachments.Count == 1 ? " [a file]" : $" [{message.Attachments.Count} files]";
        text = GameControls.OneLine(text);
        return text.Length <= Longest ? text : text[..(Longest - 1)] + "…";
    }

    private void Queue(IReadOnlyList<Bridged> to, string line)
    {
        if (to.Count > 0)
            _work.Writer.TryWrite(() => SayAsync(to, line));
    }

    private async Task SayAsync(IReadOnlyList<Bridged> to, string line)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        foreach (var bridged in to)
        {
            if (await db.GameServers.FindAsync(bridged.ServerId) is not { RconAddress: not null } server || GameControls.Say(server.Game, line) is not { } command)
                continue;
            if ((await control.RunAsync(server, command)).Problem is { } problem)
            {
                using (logger.BeginScope(DebugLog.Guild(server.GuildId)))
                    logger.LogDebug("Chat couldn't be said in {Server}: {Problem}", bridged.Name, problem);
            }
        }
    }

    // Chat a console streamed, posted through its server's webhook as the player; from there it reaches the others.
    private async Task PostAsync(GameServer server, string name, string player, string text)
    {
        if (server.BridgeWebhookId is not { } hook || details.WebhookTokenOf(server) is not { } token)
            return;
        try
        {
            await rest.ExecuteWebhookAsync(hook, token, new WebhookMessageProperties
            {
                Content = GameControls.OneLine(text),
                Username = Speakable($"{player} · {name}"),
                AllowedMentions = AllowedMentionsProperties.None,
            });
        }
        catch (RestException ex)
        {
            using (logger.BeginScope(DebugLog.Guild(server.GuildId)))
                logger.LogWarning("{Server}'s chat couldn't be posted: {Message}", name, ex.Message);
        }
    }

    // Discord refuses webhook names with "discord" or "clyde" in them.
    public static string Speakable(string name)
    {
        var named = Refused().Replace(name, m => $"{m.Value[0]}​{m.Value[1..]}");
        return named.Length <= 80 ? named : named[..80];
    }

    // A server's bridge set, its webhook made in the channel (an old one taken away); null, or why not.
    public async Task<string?> SetAsync(GameServer server, ulong? channelId, bool into)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GameServers.FindAsync(server.Id) is not { } stored)
            return "That game server is gone.";
        stored.BridgeInto = into;
        if (stored.BridgeChannelId != channelId)
        {
            await ForgetWebhookAsync(stored);
            (stored.BridgeChannelId, stored.BridgeWebhookId, stored.BridgeWebhookToken) = (null, null, null);
            if (channelId is { } channel)
            {
                try
                {
                    var hook = await rest.CreateWebhookAsync(channel, new WebhookProperties(Speakable($"{stored.Name ?? stored.Game} (game chat)")));
                    (stored.BridgeChannelId, stored.BridgeWebhookId, stored.BridgeWebhookToken) = (channel, hook.Id, details.Protect(hook.Token));
                }
                catch (RestException ex)
                {
                    await db.SaveChangesAsync();
                    await ReloadAsync();
                    return $"The bridge's webhook couldn't be made there ({ex.Message}): the bot needs Manage Webhooks in that channel.";
                }
            }
        }
        await db.SaveChangesAsync();
        await ReloadAsync();
        return null;
    }

    private async Task ForgetWebhookAsync(GameServer server)
    {
        if (server.BridgeWebhookId is not { } hook)
            return;
        try
        {
            await rest.DeleteWebhookAsync(hook);
        }
        catch (RestException ex)
        {
            logger.LogDebug("The bridge's webhook {Webhook} couldn't be deleted: {Message}", hook, ex.Message);
        }
    }

    private async Task RemovedAsync(GameServer server)
    {
        await ForgetWebhookAsync(server);
        await ReloadAsync();
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var servers = await db.GameServers.AsNoTracking().Where(s => s.BridgeChannelId != null).ToListAsync(ct);
        _bridges = servers.GroupBy(s => s.BridgeChannelId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Bridged>)[.. g.Select(s => new Bridged(s.Id, g.Key, s.Name ?? s.Game, s.Game, s.BridgeWebhookId, s.BridgeInto))]);
        Listen([.. servers.Where(s => Streams(s.Game) && s.RconAddress is not null && s.BridgeWebhookId is not null)], ct);
    }

    // Each streaming console with a bridge kept open; the rest closed.
    private void Listen(IReadOnlyList<GameServer> servers, CancellationToken ct)
    {
        foreach (var (id, (_, stop)) in _listening.Where(l => servers.All(s => s.Id != l.Key)).ToList())
        {
            stop.Cancel();
            _listening.TryRemove(id, out _);
        }
        foreach (var server in servers)
        {
            var started = $"{server.RconAddress}|{server.RconPassword}|{server.BridgeWebhookId}|{server.Name}";
            if (_listening.TryGetValue(server.Id, out var running) && running.Started == started)
                continue;
            running.Stop?.Cancel();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listening[server.Id] = (started, stop);
            _ = ListenAsync(server, stop.Token);
        }
    }

    private async Task ListenAsync(GameServer server, CancellationToken ct)
    {
        var name = server.Name ?? server.Game;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (console, problem) = await control.ConsoleAsync(server, ct);
                if (console is null)
                    throw new RconException(problem!);
                if (GameControls.Console(server.Game) == RemoteConsole.WebRcon)
                    await WebRcon.ListenAsync(console.Host, console.Port, console.Password, async m =>
                    {
                        if (RustChat(m) is var (player, text))
                            await PostAsync(server, name, player, text);
                    }, ct);
                else
                    await Telnet.ListenAsync(console.Host, console.Port, console.Password, async line =>
                    {
                        if (SevenDaysChat(line) is var (player, text))
                            await PostAsync(server, name, player, text);
                    }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                using (logger.BeginScope(DebugLog.Guild(server.GuildId)))
                    logger.LogDebug("Listening to {Server}'s chat stopped: {Message}", name, ex.Message);
            }
            try
            {
                await Task.Delay(Reconnect, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // A player's global chat as 7 Days to Die logs it; the server's own lines (entity -1) aren't a player's.
    public static (string Player, string Text)? SevenDaysChat(string line)
        => SevenDays().Match(line) is { Success: true } m && m.Groups["to"].Value == "Global" && m.Groups["entity"].Value != "-1" ? (m.Groups["name"].Value, m.Groups["text"].Value) : null;

    // A player's global chat as Rust's WebRCON sends it: the message is the chat, as JSON of its own; the server's
    // own (user 0) isn't a player's.
    public static (string Player, string Text)? RustChat(JsonElement message)
    {
        if (!message.TryGetProperty("Type", out var type) || type.GetString() != "Chat" || !message.TryGetProperty("Message", out var inner) || inner.GetString() is not { } json)
            return null;
        try
        {
            using var chat = JsonDocument.Parse(json);
            var root = chat.RootElement;
            return root.TryGetProperty("Channel", out var channel) && channel.GetInt32() == 0 && root.GetProperty("UserId").ToString() is not ("0" or "")
                ? (root.GetProperty("Username").GetString() ?? "?", root.GetProperty("Message").GetString() ?? "")
                : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        watch.Removed += RemovedAsync;
        await ReloadAsync(ct);
        await Task.WhenAll(SayingAsync(ct), ReloadingAsync(ct));
    }

    private async Task SayingAsync(CancellationToken ct)
    {
        await foreach (var work in _work.Reader.ReadAllAsync(ct))
        {
            try
            {
                await work();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Passing chat on failed");
            }
        }
    }

    // Servers changed elsewhere (a password, a name, one removed) are picked up.
    private async Task ReloadingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Reload);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await ReloadAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reloading the chat bridges failed");
            }
        }
    }

    [GeneratedRegex(@"<(?<kind>@!?|@&|#)(?<id>\d+)>")]
    private static partial Regex Mention();

    [GeneratedRegex(@"<a?:(?<name>\w+):\d+>")]
    private static partial Regex Emoji();

    [GeneratedRegex("discord|clyde", RegexOptions.IgnoreCase)]
    private static partial Regex Refused();

    [GeneratedRegex(@"Chat \(from '(?<from>[^']*)', entity id '(?<entity>-?\d+)', to '(?<to>[^']*)'\): '(?<name>.*?)': (?<text>.*)$")]
    private static partial Regex SevenDays();
}

public sealed class ChatBridgeHandler(ChatBridge bridge) : IMessageCreateGatewayHandler
{
    public ValueTask HandleAsync(Message arg)
    {
        bridge.Seen(arg);
        return default;
    }
}
