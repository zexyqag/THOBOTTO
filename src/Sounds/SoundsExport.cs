using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;

namespace THOBOTTO.Sounds;

// Every sound of the server's library, from proposal on, with its votes, and the audio files under assets/sounds/,
// for the archive's export.
public sealed class SoundsExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource, IExportFiles
{
    public string Name => "sounds";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var votes = (await db.SoundVotes.AsNoTracking().Where(v => db.Sounds.Any(s => s.GuildId == guildId && s.Id == v.SoundId)).ToListAsync()).ToLookup(v => v.SoundId);
        json.WriteStartArray();
        // The audio stays in the database until the files are written.
        var sounds = db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId).OrderBy(s => s.Id).Select(s => new
        {
            s.Id,
            s.Name,
            s.CreatorId,
            s.ProposerId,
            s.FileName,
            s.Milliseconds,
            s.State,
            s.Paid,
            s.VoteChannelId,
            s.VoteMessageId,
            s.ProposedAt,
            s.VoteEndsAt,
            s.DecidedAt,
            s.DiscordId,
            s.Plays,
            s.LastPlayedAt,
        });
        await foreach (var sound in sounds.AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", sound.Id);
            json.WriteString("name", sound.Name);
            json.WriteId("creator", sound.CreatorId);
            json.WriteId("proposer", sound.ProposerId);
            json.WriteString("fileName", sound.FileName);
            json.WriteString("file", ExportFolder.PathOf(Name, FileName(sound.Id, sound.FileName)));
            json.WriteNumber("milliseconds", sound.Milliseconds);
            json.WriteString("state", sound.State);
            json.WriteNumber("paid", sound.Paid);
            json.WriteId("voteChannel", sound.VoteChannelId);
            json.WriteId("voteMessage", sound.VoteMessageId);
            json.WriteString("proposedAt", sound.ProposedAt);
            json.WriteString("voteEndsAt", sound.VoteEndsAt);
            json.WriteTime("decidedAt", sound.DecidedAt);
            json.WriteId("discordId", sound.DiscordId);
            json.WriteNumber("plays", sound.Plays);
            json.WriteTime("lastPlayedAt", sound.LastPlayedAt);
            json.WriteStartArray("votes");
            foreach (var vote in votes[sound.Id])
            {
                json.WriteStartObject();
                json.WriteId("member", vote.UserId);
                json.WriteBoolean("up", vote.Up);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    public async Task WriteFilesAsync(ulong guildId, ExportFolder folder)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await foreach (var sound in db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId).OrderBy(s => s.Id).Select(s => new { s.Id, s.FileName, s.Audio }).AsAsyncEnumerable())
            await folder.SaveAsync(FileName(sound.Id, sound.FileName), sound.Audio);
    }

    // The id first, so two sounds uploaded under one name keep both files.
    private static string FileName(long id, string fileName) => $"{id}-{ExportFolder.Safe(Path.GetFileNameWithoutExtension(fileName))}{Path.GetExtension(fileName)}";
}
