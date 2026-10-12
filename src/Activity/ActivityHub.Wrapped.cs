using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Wrapped;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private async Task<bool> DoWrappedAsync(WebSocket socket, Window window, string? action, JsonElement value, CancellationToken ct)
    {
        if (action != "wrapped")
            return false;
        await SendAsync(socket, new JsonObject { ["wrapped"] = await WrappedAsync(window.Guild, window.Member, value) }.ToJsonString(), ct);
        return true;
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
}
