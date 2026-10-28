using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Access;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private const int QueueShown = 20;

    private async Task MusicStateAsync(JsonObject state, Window window, ulong channelId)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (music.PlayerIn(guild.Id, channelId) is not { } player)
            return;
        var queue = player.Queue.Take(QueueShown).ToList();
        var people = await names.NamesAsync(guild.Id, queue.Prepend(player.Current).OfType<Track>().Select(t => t.RequestedBy).Distinct());
        state["helper"] = player.Helper.Name;
        state["paused"] = player.Paused;
        state["loop"] = player.Loop.ToString().ToLowerInvariant();
        state["canControl"] = await music.RefusalAsync(guild.Id, member, player) is null;
        var canManage = await access.CanAsync(guild, member, BotPermissions.ManageMusic);
        var (votes, needed) = await music.SkipTallyAsync(player);
        state["skipVotes"] = new JsonObject { ["votes"] = votes, ["needed"] = needed };
        state["queue"] = new JsonArray([.. queue.Select((t, i) => (JsonNode)Song(t, people, i < player.AddedCount, canManage || t.RequestedBy == member.Id, i < player.AddedCount ? player.VotesFor(t) : null, player.VotedFor(t, member.Id)))]);
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
    }

    private static JsonObject Song(Track track, IReadOnlyDictionary<ulong, string> people, bool added, bool? canRemove = null, int? votes = null, bool voted = false) => new()
    {
        ["canRemove"] = canRemove,
        ["votes"] = votes,
        ["voted"] = voted,
        ["title"] = track.Title,
        ["author"] = track.Author,
        ["uri"] = track.Uri,
        ["art"] = track.Artwork,
        ["length"] = track.IsStream ? null : track.LengthMs,
        ["by"] = people.GetValueOrDefault(track.RequestedBy),
        ["added"] = added,
    };

    private async Task DoMusicAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        var (guild, member) = (window.Guild, window.Member);
        var player = music.PlayerIn(guild.Id, channelId);
        if (action is "add" or "pause" or "skip" or "stop" or "remove" or "vote" && !await modules.IsEnabledAsync(guild.Id, MusicService.ModuleId))
        {
            await ReplyAsync(socket, guild.Id, ModuleRegistry.Off(MusicService.ModuleId), ct);
            return;
        }
        var reply = (action, player) switch
        {
            ("add", _) when value.ValueKind == JsonValueKind.String => InChannel(guild.Id, member.Id, channelId)
                ? await music.PlayAsync(guild.Id, member.Id, channelId, player?.TextChannelId ?? channelId, value.GetString()!, Placement.Last)
                : "Join a voice channel first.",
            ("pause" or "skip" or "stop", { } playing) => await music.ButtonAsync(guild.Id, member, action!, playing.Helper.UserId),
            ("remove", { } playing) when value.ValueKind == JsonValueKind.Number => await music.RemoveAsync(guild.Id, member, playing, value.GetInt32()),
            ("vote", { } playing) when value.ValueKind == JsonValueKind.Number => await music.VoteAsync(guild.Id, member.Id, playing, value.GetInt32()),
            (_, null) => "Nothing is playing here.",
            _ => "That isn't something I can do.",
        };
        await ReplyAsync(socket, guild.Id, reply, ct);
    }
}
