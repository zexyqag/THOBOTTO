using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Access;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private async Task WatchStateAsync(JsonObject state, Window window, ulong channelId)
    {
        var (guild, member) = (window.Guild, window.Member);
        var elsewhere = watching.Playing(guild.Id).Where(p => p.ChannelId != channelId && MayWatch(guild, member, p.ChannelId)).ToList();
        // Tuned to a channel that stopped watching (or was closed to them): back home.
        if (window.TunedTo is { } tuned && !elsewhere.Any(p => p.ChannelId == tuned))
            window.TunedTo = null;
        if (watching.State(guild.Id, window.TunedTo ?? channelId) is { } watched)
        {
            if (window.TunedTo is { } away)
                watched["tunedTo"] = guild.Channels.TryGetValue(away, out var there) ? there.Name : "";
            var canManage = await access.CanAsync(guild, member, BotPermissions.ManageMusic);
            foreach (var item in (watched["queue"] as JsonArray ?? []).OfType<JsonObject>())
                item["canRemove"] = canManage || item["by"]?.GetValue<string>() == member.Id.ToString();
            state["watch"] = watched;
        }
        if (elsewhere.Count > 0)
            state["watchElsewhere"] = new JsonArray([.. elsewhere.Select(p => (JsonNode)new JsonObject
            {
                ["channel"] = p.ChannelId.ToString(),
                ["name"] = guild.Channels.TryGetValue(p.ChannelId, out var c) ? c.Name : "",
                ["title"] = p.Title,
            })]);
    }

    private async Task<bool> DoWatchAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (action == "watch-tune")
        {
            var to = value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out var id) ? id : (ulong?)null;
            if (to is { } there && there != channelId && (!watching.Playing(guild.Id).Any(p => p.ChannelId == there) || !MayWatch(guild, member, there)))
            {
                await ReplyAsync(socket, guild.Id, "Nothing to tune in to there.", ct);
                return true;
            }
            window.TunedTo = to == channelId ? null : to;
            return true;
        }
        if (action is { } named && named.StartsWith("watch-"))
        {
            var answer = !InChannel(guild.Id, member.Id, channelId) ? "Join a voice channel first."
                : window.TunedTo is not null ? "You're watching along elsewhere: come back to your channel to control what it watches."
                : named == "watch-add" && value.ValueKind == JsonValueKind.String ? await watching.AddAsync(guild, member, channelId, value.GetString()!)
                : named == "watch-remove" && value.ValueKind == JsonValueKind.Number ? await watching.RemoveAsync(guild, member, channelId, value.GetInt32())
                : await watching.ControlAsync(guild, member, channelId, named["watch-".Length..], value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
            await ReplyAsync(socket, guild.Id, answer, ct);
            return true;
        }
        return false;
    }
}
