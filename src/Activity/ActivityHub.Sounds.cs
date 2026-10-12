using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Modules;
using THOBOTTO.Sounds;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private async Task<bool> DoSoundsAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (action == "sounds")
        {
            var list = await modules.IsEnabledAsync(guild.Id, SoundBoard.ModuleId)
                ? new JsonObject
                {
                    ["list"] = new JsonArray([.. (await sounds.LibraryAsync(guild.Id)).OrderBy(s => s.Name)
                    .Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["seconds"] = Math.Round(s.Milliseconds / 1000.0, 1) })])
                }
                : new JsonObject { ["off"] = ModuleRegistry.Off(SoundBoard.ModuleId) };
            await SendAsync(socket, new JsonObject { ["sounds"] = list }.ToJsonString(), ct);
            return true;
        }
        if (action == "sound-play" && value.ValueKind == JsonValueKind.String)
        {
            await ReplyAsync(socket, guild.Id, InChannel(guild.Id, member.Id, channelId) ? await sounds.PlayAsync(guild.Id, member.Id, value.GetString()!) : "Join the voice channel to play sounds.", ct);
            return true;
        }
        return false;
    }
}
