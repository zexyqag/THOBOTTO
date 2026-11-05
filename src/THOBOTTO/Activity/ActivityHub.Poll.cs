using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Modules;
using THOBOTTO.Polls;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private void PollState(JsonObject state, Window window, ulong channelId)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (polls.In(guild.Id, channelId) is not { } live)
            return;
        var (poll, counts) = (live.Poll, live.Counts());
        state["poll"] = new JsonObject
        {
            ["question"] = poll.Question,
            ["options"] = new JsonArray([.. poll.Options.Select((o, i) => (JsonNode)new JsonObject { ["text"] = o, ["votes"] = counts[i] })]),
            ["mine"] = live.Votes.TryGetValue(member.Id, out var mine) ? mine : null,
            ["closed"] = poll.Closed,
            ["left"] = (long)Math.Max(0, (poll.ClosesAt - DateTimeOffset.UtcNow).TotalMilliseconds),
            ["asker"] = poll.AskerId == member.Id,
        };
    }

    private async Task<bool> DoPollAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        if (action is not { } polling || !polling.StartsWith("poll-"))
            return false;
        var (guild, member) = (window.Guild, window.Member);
        var open = polls.In(guild.Id, channelId) is { Poll.Closed: false } live ? live : null;
        var answer = !await modules.IsEnabledAsync(guild.Id, PollBook.ModuleId) ? ModuleRegistry.Off(PollBook.ModuleId)
            : !InChannel(guild.Id, member.Id, channelId) ? "Join the voice channel to take part."
            : polling switch
            {
                "poll-create" when value.ValueKind == JsonValueKind.Object && value.TryGetProperty("question", out var q) && value.TryGetProperty("options", out var o)
                    && q.ValueKind == JsonValueKind.String && o.ValueKind == JsonValueKind.Array => await polls.AskInVoiceAsync(guild.Id, channelId, member.Id,
                    q.GetString()!, [.. o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "")],
                    value.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 5),
                "poll-vote" when value.ValueKind == JsonValueKind.Number => open is null ? "That poll is closed." : await polls.VoteAsync(open.Poll.Id, member.Id, value.GetInt32()),
                "poll-close" => open is null ? "No poll is open here." : await polls.CloseAsync(open.Poll.Id, guild, member),
                _ => "That isn't something I can do.",
            };
        if (answer is not null)
            await ReplyAsync(socket, guild.Id, answer, ct);
        return true;
    }
}
