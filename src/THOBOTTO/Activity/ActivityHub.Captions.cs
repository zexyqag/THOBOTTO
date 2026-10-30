using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Quotes;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private async Task CaptionsStateAsync(JsonObject state, Window window, ulong channelId)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (!InChannel(guild.Id, member.Id, channelId))
            return;
        var said = transcript.Recent(channelId);
        var people = await names.NamesAsync(guild.Id, said.Select(s => s.UserId));
        var all = await prefs.AllAsync();
        state["captions"] = new JsonObject
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

    private async Task<bool> DoCaptionsAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        var (guild, member) = (window.Guild, window.Member);
        var reply = action switch
        {
            "listen" => await ears.SummonAsync(guild.Id, member.Id),
            "quote" when value.ValueKind == JsonValueKind.Array && InChannel(guild.Id, member.Id, channelId)
                => await quotes.SaveCaptionsAsync(guild.Id, channelId, member.Id, [.. value.EnumerateArray().Select(v => v.GetInt64())]),
            _ => null,
        };
        if (reply is null)
            return false;
        await ReplyAsync(socket, guild.Id, reply, ct);
        return true;
    }
}
