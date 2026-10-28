using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Activity.Watch;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Feedback;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Panel;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Activity;

// One Activity window, live: what plays in its voice channel (the track with cover and lyrics, where it is, the
// queue), the video watched together, the music quiz, a poll, the soundboard and Wrapped as stories (on request), and captions of what's said (to those in the channel only, from the
// transcript kept in memory) whenever that changes and every few seconds, and the controls it sends back, checked
// as the music buttons are.
public sealed partial class ActivityHub(
    MusicService music,
    LyricsFinder lyrics,
    PanelNames names,
    WrappedNames wrappedNames,
    PanelAccess access,
    WatchRooms watching,
    VoiceEars ears,
    VoiceTranscript transcript,
    VoicePrefs prefs,
    VoicePresence presence,
    VoiceQuotes quotes,
    QuizRooms quizzes,
    WrappedService wrapped,
    ActivityPolls polls,
    SoundBoard sounds,
    FeedbackBox feedback,
    ModuleState modules,
    ILogger<ActivityHub> logger)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(500);
    // An action is a few hundred bytes; anything larger is someone filling memory.
    private const int MessageLimit = 64 * 1024;
    private const int WindowsPerMember = 3;
    // Who the member is now (still in the server, still allowed in the channel) is asked again this often.
    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(1);

    // One window: who looks through it, as last checked, and another voice channel's video it follows, watch-only.
    private sealed class Window(Guild guild, GuildUser member)
    {
        public Guild Guild { get; set; } = guild;

        public GuildUser Member { get; set; } = member;

        public ulong? TunedTo { get; set; }
    }

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), int> _open = new();

    public async Task RunAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, CancellationToken ct)
    {
        var key = (guild.Id, member.Id);
        // Everything logged about the window is this server's.
        using var _ = logger.BeginScope(DebugLog.Guild(guild.Id));
        try
        {
            if (_open.AddOrUpdate(key, 1, (_, n) => n + 1) > WindowsPerMember)
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Too many windows.", ct);
            else
                await LiveAsync(socket, new(guild, member), channelId, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            if (_open.AddOrUpdate(key, 0, (_, n) => n - 1) == 0)
                _open.TryRemove(KeyValuePair.Create(key, 0));
        }
    }

    private async Task LiveAsync(WebSocket socket, Window window, ulong channelId, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        logger.LogDebug("Activity opened by {UserId} in {ChannelId}", window.Member.Id, channelId);
        var receiving = ReceiveAsync(socket, window, channelId, stop);
        string? sent = null;
        var sentAt = DateTimeOffset.MinValue;
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow - checkedAt >= Recheck)
                    {
                        if (await access.InAsync(window.Guild.Id, window.Member.Id) is not var (guild, member) || !MayWatch(guild, member, channelId))
                        {
                            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "That channel isn't open to you anymore.", stop.Token);
                            break;
                        }
                        (window.Guild, window.Member, checkedAt) = (guild, member, DateTimeOffset.UtcNow);
                    }
                    var state = (await StateAsync(window, channelId)).ToJsonString();
                    // The position moves on its own in the page; the rest is sent when it changes (and now and then).
                    var changed = Without(state, "position") != (sent is null ? null : Without(sent, "position"));
                    if (changed || DateTimeOffset.UtcNow - sentAt >= Every)
                    {
                        await SendAsync(socket, state, stop.Token);
                        (sent, sentAt) = (state, DateTimeOffset.UtcNow);
                    }
                }
                // A bad moment (the database, Lavalink) doesn't close the window; the next look may be fine.
                catch (Exception ex) when (ex is not OperationCanceledException and not WebSocketException)
                {
                    logger.LogDebug(ex, "Activity state for {ChannelId} failed", channelId);
                }
                await Task.Delay(Look, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        stop.Cancel();
        await receiving;
    }

    private async Task<JsonObject> StateAsync(Window window, ulong channelId)
    {
        var guild = window.Guild;
        var musicOn = await modules.IsEnabledAsync(guild.Id, MusicService.ModuleId);
        var state = new JsonObject
        {
            ["channel"] = guild.Channels.TryGetValue(channelId, out var channel) ? channel.Name : "",
            // The tabs for what's on here.
            ["on"] = new JsonObject
            {
                ["music"] = musicOn,
                ["watch"] = await modules.IsEnabledAsync(guild.Id, WatchRooms.ModuleId),
                ["quiz"] = await modules.IsEnabledAsync(guild.Id, QuizRooms.ModuleId),
                ["sounds"] = await modules.IsEnabledAsync(guild.Id, SoundBoard.ModuleId),
                ["wrapped"] = await modules.IsEnabledAsync(guild.Id, WrappedPoster.ModuleId),
            },
        };
        await WatchStateAsync(state, window, channelId);
        PollState(state, window, channelId);
        await QuizStateAsync(state, window, channelId);
        await CaptionsStateAsync(state, window, channelId);
        if (musicOn)
            await MusicStateAsync(state, window, channelId);
        return state;
    }

    // {"do": "pause" | "skip" | "stop" | "add" | "listen" | "quote" | "wrapped" | "watch-…" | "quiz-…", "value": …}
    private async Task ReceiveAsync(WebSocket socket, Window window, ulong channelId, CancellationTokenSource stop)
    {
        var buffer = new byte[4096];
        using var whole = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var got = await socket.ReceiveAsync(buffer, stop.Token);
                if (got.MessageType == WebSocketMessageType.Close)
                    break;
                whole.Write(buffer, 0, got.Count);
                if (whole.Length > MessageLimit)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "Too big.", stop.Token);
                    break;
                }
                if (!got.EndOfMessage)
                    continue;
                string? action = null;
                try
                {
                    using var message = JsonDocument.Parse(whole.ToArray());
                    action = message.RootElement.GetProperty("do").GetString();
                    var value = message.RootElement.TryGetProperty("value", out var v) ? v : default;
                    await DoAsync(socket, window, channelId, action, value, stop.Token);
                }
                // One action going wrong doesn't end the connection.
                catch (Exception ex) when (ex is not OperationCanceledException and not WebSocketException)
                {
                    logger.LogWarning(ex, "Activity action {Action} in {ChannelId} failed", action, channelId);
                    await ReplyAsync(socket, window.Guild.Id, "That didn't work; it's in the bot's log.", stop.Token);
                }
                whole.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            logger.LogDebug("Activity connection ended: {Message}", ex.Message);
        }
        stop.Cancel();
    }

    private async Task DoAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (action != "client-log")
            logger.LogDebug("Activity: {Action} by {UserId} in {ChannelId}", action, member.Id, channelId);
        // What went wrong in the page (a video that won't play), for the bot's log.
        if (action == "client-log" && value.ValueKind == JsonValueKind.String)
        {
            var said = value.GetString()!;
            logger.LogWarning("Activity of {UserId} in {ChannelId}: {Message}", member.Id, channelId, said.Length > 300 ? said[..300] : said);
            return;
        }
        if (action == "feedback" && value.ValueKind == JsonValueKind.String)
        {
            await ReplyAsync(socket, guild.Id, await feedback.AddAsync(guild.Id, guild.Name, member.Id, member.Nickname ?? member.GlobalName ?? member.Username, value.GetString()!, "activity"), ct);
            return;
        }
        if (await DoSoundsAsync(socket, window, channelId, action, value, ct)
            || await DoWrappedAsync(socket, window, action, value, ct)
            || await DoPollAsync(socket, window, channelId, action, value, ct)
            || await DoQuizAsync(socket, window, channelId, action, value, ct)
            || await DoWatchAsync(socket, window, channelId, action, value, ct)
            || await DoCaptionsAsync(socket, window, channelId, action, value, ct))
            return;
        // The music player answers its own actions, and whatever no tab took.
        await DoMusicAsync(socket, window, channelId, action, value, ct);
    }

    // As a line of text: mentions become names.
    private async Task ReplyAsync(WebSocket socket, ulong guildId, string reply, CancellationToken ct)
    {
        var mentioned = Mention().Matches(reply).Select(m => ulong.Parse(m.Groups[1].Value)).ToList();
        var people = await names.NamesAsync(guildId, mentioned);
        var plain = Mention().Replace(reply, m => people.GetValueOrDefault(ulong.Parse(m.Groups[1].Value)) ?? "someone");
        await SendAsync(socket, new JsonObject { ["reply"] = plain }.ToJsonString(), ct);
    }

    // The state, the replies and the close go out one at a time: a WebSocket sends one message at once.
    private readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> _sending = [];

    private Task SendAsync(WebSocket socket, string json, CancellationToken ct)
        => OneAtATimeAsync(socket, () => socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct), ct);

    // Only our side closes; waiting for the page's answer would let a hostile one keep the connection.
    private Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string why, CancellationToken ct)
        => OneAtATimeAsync(socket, () => socket.CloseOutputAsync(status, why, ct), ct);

    private async Task OneAtATimeAsync(WebSocket socket, Func<Task> send, CancellationToken ct)
    {
        var gate = _sending.GetValue(socket, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await send();
        }
        finally
        {
            gate.Release();
        }
    }

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex Mention();

    // The Activity (opening it, tuning in) is for voice and stage channels they could join themselves.
    public static bool MayWatch(Guild guild, GuildUser member, ulong channelId)
        => guild.Channels.TryGetValue(channelId, out var channel)
            && channel is IVoiceGuildChannel
            && member.GetChannelPermissions(guild, channel) is var allowed
            && (allowed & (Permissions.ViewChannel | Permissions.Connect)) == (Permissions.ViewChannel | Permissions.Connect);

    private bool InChannel(ulong guildId, ulong userId, ulong channelId)
        => presence.Snapshot(guildId).TryGetValue(userId, out var here) && here.ChannelId == channelId;

    // Without the positions (the music's and the video's) and the quiz's time left, which move on their own.
    private static string Without(string json, string key)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(key);
        (node["watch"] as JsonObject)?.Remove(key);
        (node["quiz"] as JsonObject)?.Remove("left");
        (node["poll"] as JsonObject)?.Remove("left");
        return node.ToJsonString();
    }
}
