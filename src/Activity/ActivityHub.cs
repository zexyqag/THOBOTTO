using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Listening;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Panel;
using THOBOTTO.Quiz;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Stats;
using THOBOTTO.Voice;
using THOBOTTO.Watch;

namespace THOBOTTO.Activity;

// One Activity window, live: what plays in its voice channel (the track with cover and lyrics, where it is, the
// queue), the video watched together, the music quiz, a poll, the soundboard and Wrapped as stories (on request), and captions of what's said (to those in the channel only, from the
// transcript kept in memory) whenever that changes and every few seconds, and the controls it sends back, checked
// as the music buttons are.
public sealed partial class ActivityHub(
    MusicService music,
    LyricsFinder lyrics,
    PanelNames names,
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
    ModuleState modules,
    ILogger<ActivityHub> logger)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(500);
    private const int QueueShown = 20;

    // Another voice channel's video this window follows, watch-only.
    private sealed class Tuning
    {
        public ulong? ChannelId { get; set; }
    }

    public async Task RunAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tuning = new Tuning();
        var receiving = ReceiveAsync(socket, guild, member, channelId, tuning, stop);
        string? sent = null;
        var sentAt = DateTimeOffset.MinValue;
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var state = (await StateAsync(guild, member, channelId, tuning)).ToJsonString();
                // The position moves on its own in the page; the rest is sent when it changes (and now and then).
                var changed = Without(state, "position") != (sent is null ? null : Without(sent, "position"));
                if (changed || DateTimeOffset.UtcNow - sentAt >= Every)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(state), WebSocketMessageType.Text, true, stop.Token);
                    (sent, sentAt) = (state, DateTimeOffset.UtcNow);
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

    private async Task<JsonObject> StateAsync(Guild guild, GuildUser member, ulong channelId, Tuning tuning)
    {
        var state = new JsonObject { ["channel"] = guild.Channels.TryGetValue(channelId, out var channel) ? channel.Name : "" };
        var elsewhere = watching.Playing(guild.Id).Where(p => p.ChannelId != channelId && MayWatch(guild, member, p.ChannelId)).ToList();
        // Tuned to a channel that stopped watching (or was closed to them): back home.
        if (tuning.ChannelId is { } tuned && !elsewhere.Any(p => p.ChannelId == tuned))
            tuning.ChannelId = null;
        if (watching.State(guild.Id, tuning.ChannelId ?? channelId) is { } watched)
        {
            if (tuning.ChannelId is { } away)
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
        if (music.PlayerIn(guild.Id, channelId) is not { } player)
            return state;
        var queue = player.Queue.Take(QueueShown).ToList();
        var people = await names.NamesAsync(guild.Id, queue.Prepend(player.Current).OfType<Track>().Select(t => t.RequestedBy).Distinct());
        state["helper"] = player.Helper.Name;
        state["paused"] = player.Paused;
        state["volume"] = player.Volume;
        state["loop"] = player.Loop.ToString().ToLowerInvariant();
        state["canControl"] = await music.RefusalAsync(guild.Id, member, player) is null;
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
            ? await wrapped.ServerAsync(guild, period, PanelNames.Mark)
            : await wrapped.MemberAsync(guild, member.Id, "Your", period, PanelNames.Mark);
        var named = await Task.WhenAll(cards.Select(c => names.WrappedAsync(guild.Id, c)));
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
    private async Task ReceiveAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, Tuning tuning, CancellationTokenSource stop)
    {
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var got = await socket.ReceiveAsync(buffer, stop.Token);
                if (got.MessageType == WebSocketMessageType.Close)
                    break;
                using var message = JsonDocument.Parse(buffer.AsMemory(0, got.Count));
                var action = message.RootElement.GetProperty("do").GetString();
                var value = message.RootElement.TryGetProperty("value", out var v) ? v : default;
                await DoAsync(socket, guild, member, channelId, tuning, action, value, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogDebug("Activity connection ended: {Message}", ex.Message);
        }
        stop.Cancel();
    }

    private async Task DoAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, Tuning tuning, string? action, JsonElement value, CancellationToken ct)
    {
        var player = music.PlayerIn(guild.Id, channelId);
        if (action == "sounds")
        {
            var list = await modules.IsEnabledAsync(guild.Id, SoundBoard.ModuleId)
                ? new JsonObject { ["list"] = new JsonArray([.. (await sounds.LibraryAsync(guild.Id)).OrderBy(s => s.Name)
                    .Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["seconds"] = Math.Round(s.Milliseconds / 1000.0, 1) })]) }
                : new JsonObject { ["off"] = $"The `{SoundBoard.ModuleId}` module is off." };
            await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["sounds"] = list }.ToJsonString()), WebSocketMessageType.Text, true, ct);
            return;
        }
        if (action == "sound-play" && value.ValueKind == JsonValueKind.String)
        {
            await ReplyAsync(socket, guild.Id, InChannel(guild.Id, member.Id, channelId) ? await sounds.PlayAsync(guild.Id, member.Id, value.GetString()!) : "Join the voice channel to play sounds.", ct);
            return;
        }
        if (action == "wrapped")
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["wrapped"] = await WrappedAsync(guild, member, value) }.ToJsonString()), WebSocketMessageType.Text, true, ct);
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
                "quiz-start" when InChannel(guild.Id, member.Id, channelId) => await quizzes.StartAsync(guild.Id, channelId, member.Id),
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
            tuning.ChannelId = to == channelId ? null : to;
            return;
        }
        if (action is { } named && named.StartsWith("watch-"))
        {
            var answer = tuning.ChannelId is not null ? "You're watching along elsewhere: come back to your channel to control what it watches."
                : named == "watch-add" && value.ValueKind == JsonValueKind.String ? await watching.AddAsync(guild.Id, channelId, member.Id, value.GetString()!)
                : await watching.ControlAsync(guild, member, channelId, named["watch-".Length..], value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
            await ReplyAsync(socket, guild.Id, answer, ct);
            return;
        }
        string reply = (action, player) switch
        {
            ("listen", _) => await ears.SummonAsync(guild.Id, member.Id),
            ("quote", _) when value.ValueKind == JsonValueKind.Array && InChannel(guild.Id, member.Id, channelId)
                => await quotes.SaveCaptionsAsync(guild.Id, channelId, member.Id, [.. value.EnumerateArray().Select(v => v.GetInt64())]),
            ("add", _) when value.ValueKind == JsonValueKind.String => await music.PlayAsync(guild.Id, member.Id, channelId, player?.TextChannelId ?? channelId, value.GetString()!, Placement.Last),
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
        await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["reply"] = plain }.ToJsonString()), WebSocketMessageType.Text, true, ct);
    }

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex Mention();

    // Tuning in is for voice channels they could join themselves.
    private static bool MayWatch(Guild guild, GuildUser member, ulong channelId)
        => guild.Channels.TryGetValue(channelId, out var channel)
            && member.GetChannelPermissions(guild, channel) is var allowed
            && (allowed & (Permissions.ViewChannel | Permissions.Connect)) == (Permissions.ViewChannel | Permissions.Connect);

    private bool InChannel(ulong guildId, ulong userId, ulong channelId)
        => presence.Snapshot(guildId).TryGetValue(userId, out var here) && here.ChannelId == channelId;

    private async Task<string> VolumeAsync(Guild guild, GuildUser member, MusicPlayer player, int volume)
    {
        if (await music.RefusalAsync(guild.Id, member, player) is { } refusal)
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
