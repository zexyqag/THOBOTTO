using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Games;

// Every game of the server with its modes, and the role pickers, for the archive's export. Sessions are events
// with a game, in the events file.
public sealed class GamesExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "games";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var modes = (await db.GameModes.AsNoTracking().Where(m => db.Games.Any(g => g.GuildId == guildId && g.Id == m.GameId)).OrderBy(m => m.Id).ToListAsync()).ToLookup(m => m.GameId);
        json.WriteStartObject();
        json.WriteStartArray("games");
        await foreach (var game in db.Games.AsNoTracking().Where(g => g.GuildId == guildId).OrderBy(g => g.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", game.Id);
            json.WriteString("name", game.Name);
            json.WriteString("emoji", game.Emoji);
            json.WriteId("role", game.RoleId);
            json.WriteNumber("server", game.ServerId);
            json.WriteId("channel", game.ChannelId);
            json.WriteNumber("players", game.Players);
            json.WriteString("createdAt", game.CreatedAt);
            json.WriteStartArray("modes");
            foreach (var mode in modes[game.Id])
            {
                json.WriteStartObject();
                json.WriteNumber("id", mode.Id);
                json.WriteString("name", mode.Name);
                json.WriteNumber("players", mode.Players);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("pickers");
        await foreach (var picker in db.GamePickers.AsNoTracking().Where(p => p.GuildId == guildId).OrderBy(p => p.MessageId).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("message", picker.MessageId);
            json.WriteId("channel", picker.ChannelId);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
