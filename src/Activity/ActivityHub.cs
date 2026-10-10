using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Music;
using THOBOTTO.Panel;

namespace THOBOTTO.Activity;

// One Activity window, live: what plays in its voice channel (the track with cover and lyrics, where it is, the
// queue) whenever that changes and every few seconds, and the controls it sends back, checked as the music
// buttons are.
public sealed class ActivityHub(MusicService music, LyricsFinder lyrics, PanelNames names, THOBOTTO.Watch.WatchRooms watching, ILogger<ActivityHub> logger)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(500);
    private const int QueueShown = 20;

    public async Task RunAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiving = ReceiveAsync(socket, guild, member, channelId, stop);
        string? sent = null;
        var sentAt = DateTimeOffset.MinValue;
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var state = (await StateAsync(guild, member, channelId)).ToJsonString();
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

    private async Task<JsonObject> StateAsync(Guild guild, GuildUser member, ulong channelId)
    {
        var state = new JsonObject { ["channel"] = guild.Channels.TryGetValue(channelId, out var channel) ? channel.Name : "" };
        if (watching.State(guild.Id, channelId) is { } watched)
            state["watch"] = watched;
        if (music.PlayerIn(guild.Id, channelId) is not { } player)
            return state;
        var queue = player.Queue.Take(QueueShown).ToList();
        var people = await names.NamesAsync(guild.Id, queue.Prepend(player.Current).OfType<Track>().Select(t => t.RequestedBy).Distinct());
        state["helper"] = player.Helper.Name;
        state["paused"] = player.Paused;
        state["volume"] = player.Volume;
        state["loop"] = player.Loop.ToString().ToLowerInvariant();
        state["canControl"] = await music.RefusalAsync(guild.Id, member, player) is null;
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

    // {"do": "pause" | "skip" | "stop" | "add" | "volume", "value": …}
    private async Task ReceiveAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, CancellationTokenSource stop)
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
                await DoAsync(socket, guild, member, channelId, action, value, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogDebug("Activity connection ended: {Message}", ex.Message);
        }
        stop.Cancel();
    }

    private async Task DoAsync(WebSocket socket, Guild guild, GuildUser member, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        var player = music.PlayerIn(guild.Id, channelId);
        if (action is { } named && named.StartsWith("watch-"))
        {
            var answer = named == "watch-add" && value.ValueKind == JsonValueKind.String
                ? await watching.AddAsync(guild.Id, channelId, member.Id, value.GetString()!)
                : await watching.ControlAsync(guild, member, channelId, named["watch-".Length..], value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
            await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["reply"] = answer }.ToJsonString()), WebSocketMessageType.Text, true, ct);
            return;
        }
        string reply = (action, player) switch
        {
            ("add", _) when value.ValueKind == JsonValueKind.String => await music.PlayAsync(guild.Id, member.Id, channelId, player?.TextChannelId ?? channelId, value.GetString()!, Placement.Last),
            ("pause" or "skip" or "stop", { } playing) => await music.ButtonAsync(guild.Id, member, action!, playing.Helper.UserId),
            ("volume", { } playing) when value.ValueKind == JsonValueKind.Number => await VolumeAsync(guild, member, playing, value.GetInt32()),
            (_, null) => "Nothing is playing here.",
            _ => "That isn't something I can do.",
        };
        await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["reply"] = reply }.ToJsonString()), WebSocketMessageType.Text, true, ct);
    }

    private async Task<string> VolumeAsync(Guild guild, GuildUser member, MusicPlayer player, int volume)
    {
        if (await music.RefusalAsync(guild.Id, member, player) is { } refusal)
            return refusal;
        await player.SetVolumeAsync(Math.Clamp(volume, 0, 200));
        return $"🔊 Volume {player.Volume}%.";
    }

    // Without the positions (the music's and the video's), which move on their own.
    private static string Without(string json, string key)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(key);
        (node["watch"] as JsonObject)?.Remove(key);
        return node.ToJsonString();
    }
}
