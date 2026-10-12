using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Discord;
using THOBOTTO.Feedback;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Panel;
using THOBOTTO.Quiz;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Watch;
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
    private const int QueueShown = 20;
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
        var (guild, member) = (window.Guild, window.Member);
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
        var elsewhere = watching.Playing(guild.Id).Where(p => p.ChannelId != channelId && MayWatch(guild, member, p.ChannelId)).ToList();
        // Tuned to a channel that stopped watching (or was closed to them): back home.
        if (window.TunedTo is { } tuned && !elsewhere.Any(p => p.ChannelId == tuned))
            window.TunedTo = null;
        if (watching.State(guild.Id, window.TunedTo ?? channelId) is { } watched)
        {
            if (window.TunedTo is { } away)
                watched["tunedTo"] = guild.Channels.TryGetValue(away, out var there) ? there.Name : "";
            state["watch"] = watched;
        }
        if (elsewhere.Count > 0)
            state["watchElsewhere"] = new JsonArray([.. elsewhere.Select(p => (JsonNode)new JsonObject
            {
                ["channel"] = p.ChannelId.ToString(),
                ["name"] = guild.Channels.TryGetValue(p.ChannelId, out var c) ? c.Name : "",
                ["title"] = p.Title,
            })]);
        if (polls.In(guild.Id, channelId) is { } poll)
        {
            var counts = poll.Counts();
            state["poll"] = new JsonObject
            {
                ["question"] = poll.Question,
                ["options"] = new JsonArray([.. poll.Options.Select((o, i) => (JsonNode)new JsonObject { ["text"] = o, ["votes"] = counts[i] })]),
                ["mine"] = poll.Votes.TryGetValue(member.Id, out var mine) ? mine : null,
                ["closed"] = poll.Closed,
                ["left"] = (long)Math.Max(0, (poll.ClosesAt - DateTimeOffset.UtcNow).TotalMilliseconds),
                ["asker"] = poll.CreatedBy == member.Id,
            };
        }
        if (quizzes.State(guild.Id, channelId, member.Id) is { } quiz)
            state["quiz"] = await QuizAsync(guild, member, channelId, quiz);
        if (InChannel(guild.Id, member.Id, channelId))
            state["captions"] = await CaptionsAsync(guild, member, channelId);
        if (!musicOn || music.PlayerIn(guild.Id, channelId) is not { } player)
            return state;
        var queue = player.Queue.Take(QueueShown).ToList();
        var people = await names.NamesAsync(guild.Id, queue.Prepend(player.Current).OfType<Track>().Select(t => t.RequestedBy).Distinct());
        state["helper"] = player.Helper.Name;
        state["paused"] = player.Paused;
        state["volume"] = player.Volume;
        state["loop"] = player.Loop.ToString().ToLowerInvariant();
        state["canControl"] = await music.RefusalAsync(guild.Id, member, player) is null;
        state["canVolume"] = await music.VolumeRefusalAsync(guild.Id, member, player) is null;
        var (votes, needed) = await music.SkipTallyAsync(player);
        state["skipVotes"] = new JsonObject { ["votes"] = votes, ["needed"] = needed };
        state["queue"] = new JsonArray([.. queue.Select((t, i) => (JsonNode)Song(t, people, i < player.AddedCount))]);
        if (player.Current is { } track)
        {
            state["track"] = Song(track, people, true);
            state["position"] = player.Position;
            if (await lyrics.FindAsync(track) is { } found)
                state["lyrics"] = new JsonObject
                {
                    ["text"] = found.Text,
                    ["lines"] = new JsonArray([.. found.Lines.Select(l => (JsonNode)new JsonObject { ["at"] = l.At, ["text"] = l.Text })]),
                };
        }
        return state;
    }

    // {"who": "me" | "server", "period": …}: the periods that may be seen, and the cards for the one asked (or the
    // latest that may be seen).
    private async Task<JsonObject> WrappedAsync(Guild guild, GuildUser member, JsonElement value)
    {
        if (!await modules.IsEnabledAsync(guild.Id, WrappedPoster.ModuleId))
            return new() { ["off"] = $"The `{WrappedPoster.ModuleId}` module is off." };
        var periods = await wrapped.RevealedAsync(guild.Id);
        var who = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("who", out var w) && w.GetString() == "server" ? "server" : "me";
        var period = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("period", out var p) && Enum.TryParse<WrappedPeriod>(p.GetString(), out var asked) && periods.Contains(asked) ? asked
            : new[] { WrappedPeriod.ThisYear, WrappedPeriod.LastMonth, WrappedPeriod.LastYear }.FirstOrDefault(periods.Contains, periods.FirstOrDefault());
        var cards = who == "server"
            ? await wrapped.ServerAsync(guild, period, WrappedNames.Mark)
            : await wrapped.MemberAsync(guild, member.Id, "Your", period, WrappedNames.Mark);
        var named = await Task.WhenAll(cards.Select(c => wrappedNames.FillAsync(guild.Id, c)));
        return new()
        {
            ["who"] = who,
            ["period"] = period.ToString(),
            ["periods"] = new JsonArray([.. periods.Select(x => (JsonNode)x.ToString())]),
            ["cards"] = new JsonArray([.. named.Select(c => (JsonNode)new JsonObject
            {
                ["title"] = c.Title,
                ["intro"] = c.Intro,
                ["color"] = c.Color,
                ["facts"] = new JsonArray([.. c.Facts.Select(f => (JsonNode)new JsonObject { ["label"] = f.Label, ["value"] = f.Value })]),
                ["lists"] = new JsonArray([.. c.Lists.Select(l => (JsonNode)new JsonObject
                {
                    ["heading"] = l.Heading,
                    ["lines"] = new JsonArray([.. l.Lines.Select(x => (JsonNode)x)]),
                })]),
            })]),
        };
    }

    private async Task<JsonObject> QuizAsync(Guild guild, GuildUser member, ulong channelId, QuizState quiz)
    {
        var people = await names.NamesAsync(guild.Id, quiz.Scores.Select(s => s.UserId));
        return new()
        {
            ["phase"] = quiz.Phase.ToString().ToLowerInvariant(),
            ["round"] = quiz.Number,
            ["rounds"] = quiz.Rounds,
            ["left"] = quiz.LeftMs,
            ["choices"] = new JsonArray([.. quiz.Choices.Select(c => (JsonNode)c)]),
            ["mine"] = quiz.Mine,
            ["answered"] = quiz.Answered,
            ["players"] = quiz.Players,
            ["correct"] = quiz.Correct,
            ["song"] = quiz.Song is { } song ? $"{song.Title} — {song.Artist}" : null,
            ["art"] = quiz.Art,
            ["right"] = new JsonArray([.. quiz.Picks.Where(p => p.Gain > 0).OrderByDescending(p => p.Gain)
                .Select(p => (JsonNode)new JsonObject { ["name"] = people.GetValueOrDefault(p.UserId) ?? "Someone", ["gain"] = p.Gain })]),
            ["gain"] = quiz.Picks.FirstOrDefault(p => p.UserId == member.Id)?.Gain,
            ["scores"] = new JsonArray([.. quiz.Scores.Take(10).Select(s => (JsonNode)new JsonObject
            {
                ["name"] = people.GetValueOrDefault(s.UserId) ?? "Someone",
                ["score"] = s.Score,
                ["me"] = s.UserId == member.Id,
            })]),
            ["canStop"] = await quizzes.CanStopAsync(guild.Id, channelId, member),
        };
    }

    private async Task<JsonObject> CaptionsAsync(Guild guild, GuildUser member, ulong channelId)
    {
        var said = transcript.Recent(channelId);
        var people = await names.NamesAsync(guild.Id, said.Select(s => s.UserId));
        var all = await prefs.AllAsync();
        return new()
        {
            ["listening"] = ears.ListensIn(channelId),
            ["heard"] = all.GetValueOrDefault((guild.Id, member.Id))?.Listen == true,
            ["canQuote"] = await modules.IsEnabledAsync(guild.Id, QuoteCommands.ModuleId),
            ["lines"] = new JsonArray([.. said.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["name"] = people.GetValueOrDefault(s.UserId) ?? "Someone",
                ["text"] = s.Text,
                ["at"] = s.At.ToUnixTimeMilliseconds(),
                ["quotable"] = s.UserId == member.Id || all.GetValueOrDefault((guild.Id, s.UserId))?.QuotesOff != true,
            })]),
        };
    }

    private static JsonObject Song(Track track, IReadOnlyDictionary<ulong, string> people, bool added) => new()
    {
        ["title"] = track.Title,
        ["author"] = track.Author,
        ["uri"] = track.Uri,
        ["art"] = track.Artwork,
        ["length"] = track.IsStream ? null : track.LengthMs,
        ["by"] = people.GetValueOrDefault(track.RequestedBy),
        ["added"] = added,
    };

    // {"do": "pause" | "skip" | "stop" | "add" | "volume" | "listen" | "quote" | "wrapped" | "watch-…" | "quiz-…", "value": …}
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
        var player = music.PlayerIn(guild.Id, channelId);
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
        if (action == "sounds")
        {
            var list = await modules.IsEnabledAsync(guild.Id, SoundBoard.ModuleId)
                ? new JsonObject
                {
                    ["list"] = new JsonArray([.. (await sounds.LibraryAsync(guild.Id)).OrderBy(s => s.Name)
                    .Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["seconds"] = Math.Round(s.Milliseconds / 1000.0, 1) })])
                }
                : new JsonObject { ["off"] = $"The `{SoundBoard.ModuleId}` module is off." };
            await SendAsync(socket, new JsonObject { ["sounds"] = list }.ToJsonString(), ct);
            return;
        }
        if (action == "sound-play" && value.ValueKind == JsonValueKind.String)
        {
            await ReplyAsync(socket, guild.Id, InChannel(guild.Id, member.Id, channelId) ? await sounds.PlayAsync(guild.Id, member.Id, value.GetString()!) : "Join the voice channel to play sounds.", ct);
            return;
        }
        if (action == "wrapped")
        {
            await SendAsync(socket, new JsonObject { ["wrapped"] = await WrappedAsync(guild, member, value) }.ToJsonString(), ct);
            return;
        }
        if (action is { } polling && polling.StartsWith("poll-"))
        {
            var answer = !InChannel(guild.Id, member.Id, channelId) ? "Join the voice channel to take part."
                : polling switch
                {
                    "poll-create" when value.ValueKind == JsonValueKind.Object && value.TryGetProperty("question", out var q) && value.TryGetProperty("options", out var o)
                        && q.ValueKind == JsonValueKind.String && o.ValueKind == JsonValueKind.Array => polls.Create(guild.Id, channelId, member.Id,
                        q.GetString()!, [.. o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "")],
                        value.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 5),
                    "poll-vote" when value.ValueKind == JsonValueKind.Number => polls.Vote(guild.Id, channelId, member.Id, value.GetInt32()),
                    "poll-close" => await polls.CloseAsync(guild.Id, channelId, member.Id),
                    _ => "That isn't something I can do.",
                };
            if (answer is not null)
                await ReplyAsync(socket, guild.Id, answer, ct);
            return;
        }
        if (action is { } quizzing && quizzing.StartsWith("quiz-"))
        {
            var answer = quizzing switch
            {
                "quiz-start" when InChannel(guild.Id, member.Id, channelId) => await quizzes.StartAsync(guild.Id, channelId, member),
                "quiz-answer" when value.ValueKind == JsonValueKind.Number && InChannel(guild.Id, member.Id, channelId)
                    => await quizzes.AnswerAsync(guild.Id, channelId, member.Id, value.GetInt32()),
                "quiz-stop" => await quizzes.StopAsync(guild.Id, channelId, member),
                _ => "Join the voice channel to play.",
            };
            if (answer is not null)
                await ReplyAsync(socket, guild.Id, answer, ct);
            return;
        }
        if (action == "watch-tune")
        {
            var to = value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out var id) ? id : (ulong?)null;
            if (to is { } there && there != channelId && (!watching.Playing(guild.Id).Any(p => p.ChannelId == there) || !MayWatch(guild, member, there)))
            {
                await ReplyAsync(socket, guild.Id, "Nothing to tune in to there.", ct);
                return;
            }
            window.TunedTo = to == channelId ? null : to;
            return;
        }
        if (action is { } named && named.StartsWith("watch-"))
        {
            var answer = !InChannel(guild.Id, member.Id, channelId) ? "Join a voice channel first."
                : window.TunedTo is not null ? "You're watching along elsewhere: come back to your channel to control what it watches."
                : named == "watch-add" && value.ValueKind == JsonValueKind.String ? await watching.AddAsync(guild, member, channelId, value.GetString()!)
                : await watching.ControlAsync(guild, member, channelId, named["watch-".Length..], value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
            await ReplyAsync(socket, guild.Id, answer, ct);
            return;
        }
        if (action is "add" or "pause" or "skip" or "stop" or "volume" && !await modules.IsEnabledAsync(guild.Id, MusicService.ModuleId))
        {
            await ReplyAsync(socket, guild.Id, $"The `{MusicService.ModuleId}` module is off.", ct);
            return;
        }
        var reply = (action, player) switch
        {
            ("listen", _) => await ears.SummonAsync(guild.Id, member.Id),
            ("quote", _) when value.ValueKind == JsonValueKind.Array && InChannel(guild.Id, member.Id, channelId)
                => await quotes.SaveCaptionsAsync(guild.Id, channelId, member.Id, [.. value.EnumerateArray().Select(v => v.GetInt64())]),
            ("add", _) when value.ValueKind == JsonValueKind.String => InChannel(guild.Id, member.Id, channelId)
                ? await music.PlayAsync(guild.Id, member.Id, channelId, player?.TextChannelId ?? channelId, value.GetString()!, Placement.Last)
                : "Join a voice channel first.",
            ("pause" or "skip" or "stop", { } playing) => await music.ButtonAsync(guild.Id, member, action!, playing.Helper.UserId),
            ("volume", { } playing) when value.ValueKind == JsonValueKind.Number => await VolumeAsync(guild, member, playing, value.GetInt32()),
            (_, null) => "Nothing is playing here.",
            _ => "That isn't something I can do.",
        };
        await ReplyAsync(socket, guild.Id, reply, ct);
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

    private async Task<string> VolumeAsync(Guild guild, GuildUser member, MusicPlayer player, int volume)
    {
        if (await music.VolumeRefusalAsync(guild.Id, member, player) is { } refusal)
            return refusal;
        await player.SetVolumeAsync(Math.Clamp(volume, 0, 200));
        return $"🔊 Volume {player.Volume}%.";
    }

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
