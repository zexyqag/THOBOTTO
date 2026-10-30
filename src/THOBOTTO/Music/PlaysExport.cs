using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Music;

// Every song played in the server with who heard it, and the saved playlists, for the archive's export.
public sealed class PlaysExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    private const int Batch = 1000;

    public string Name => "plays";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("plays");
        long after = 0;
        while (true)
        {
            var plays = await db.PlayRecords.AsNoTracking().Where(p => p.GuildId == guildId && p.Id > after).OrderBy(p => p.Id).Take(Batch).ToListAsync();
            if (plays.Count == 0)
                break;
            var ids = plays.Select(p => p.Id).ToList();
            var listeners = (await db.PlayListeners.AsNoTracking().Where(l => ids.Contains(l.PlayId)).ToListAsync()).ToLookup(l => l.PlayId);
            foreach (var play in plays)
            {
                json.WriteStartObject();
                json.WriteNumber("id", play.Id);
                json.WriteId("voiceChannel", play.VoiceChannelId);
                json.WriteId("helper", play.HelperId);
                json.WriteString("personality", play.Personality);
                json.WriteString("artist", play.Artist);
                json.WriteString("title", play.Title);
                json.WriteString("source", play.Source);
                json.WriteString("uri", play.Uri);
                json.WriteNumber("lengthMs", play.LengthMs);
                json.WriteNumber("playedMs", play.PlayedMs);
                json.WriteBoolean("skipped", play.Skipped);
                json.WriteBoolean("autoplay", play.Autoplay);
                json.WriteId("requestedBy", play.RequestedBy);
                json.WriteString("startedAt", play.StartedAt);
                json.WriteStartArray("listeners");
                foreach (var listener in listeners[play.Id])
                    json.WriteIdValue(listener.UserId);
                json.WriteEndArray();
                json.WriteEndObject();
            }
            await json.FlushAsync();
            after = plays[^1].Id;
        }
        json.WriteEndArray();
        json.WriteStartArray("playlists");
        await foreach (var playlist in db.SavedPlaylists.AsNoTracking().Where(p => p.GuildId == guildId).OrderBy(p => p.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", playlist.Id);
            json.WriteString("name", playlist.Name);
            json.WriteId("creator", playlist.CreatorId);
            json.WriteNumber("trackCount", playlist.TrackCount);
            json.WriteString("updatedAt", playlist.UpdatedAt);
            json.WriteJson("tracks", playlist.Tracks);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
