using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;

namespace THOBOTTO.Archive;

// A member's own share of the archive: their messages, whole but for who else deleted one, the reactions they gave
// and the votes they cast.
public sealed class ArchiveMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    private const int Batch = 1000;

    public string Name => "Messages";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ArchivedMessages.LongCountAsync(m => m.GuildId == guildId && m.AuthorId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("messages");
        var after = 0UL;
        while (true)
        {
            var batch = await db.ArchivedMessages.AsNoTracking()
                .Where(m => m.GuildId == guildId && m.AuthorId == userId && m.Id > after)
                .OrderBy(m => m.Id).Take(Batch).ToListAsync();
            if (batch.Count == 0)
                break;
            var ids = batch.Select(m => m.Id).ToList();
            var versions = (await db.MessageVersions.AsNoTracking().Where(v => ids.Contains(v.MessageId)).ToListAsync()).ToLookup(v => v.MessageId);
            var attachments = (await db.ArchivedAttachments.AsNoTracking().Where(a => ids.Contains(a.MessageId)).ToListAsync()).ToLookup(a => a.MessageId);
            var reactions = (await db.ArchivedReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId)).ToListAsync()).ToLookup(r => r.MessageId);
            var votes = (await db.ArchivedPollVotes.AsNoTracking().Where(v => ids.Contains(v.MessageId)).ToListAsync()).ToLookup(v => v.MessageId);
            foreach (var message in batch)
                ThobottoWriter.WriteMessage(json, message, [.. versions[message.Id]], [.. attachments[message.Id]], [.. reactions[message.Id]], [.. votes[message.Id]], _ => null, namesDeleter: false);
            await json.FlushAsync();
            after = ids[^1];
        }
        json.WriteEndArray();

        json.WriteStartArray("reactionsGiven");
        var written = 0;
        await foreach (var reaction in db.ArchivedReactions.AsNoTracking()
            .Where(r => r.UserId == userId && db.ArchivedMessages.Any(m => m.Id == r.MessageId && m.GuildId == guildId))
            .OrderBy(r => r.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("message", reaction.MessageId);
            json.WriteId("emojiId", reaction.EmojiId);
            json.WriteString("emojiName", reaction.EmojiName);
            json.WriteBoolean("burst", reaction.Burst);
            json.WriteString("addedAt", reaction.AddedAt);
            json.WriteTime("removedAt", reaction.RemovedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();

        json.WriteStartArray("votesGiven");
        written = 0;
        await foreach (var vote in db.ArchivedPollVotes.AsNoTracking()
            .Where(v => v.UserId == userId && db.ArchivedMessages.Any(m => m.Id == v.MessageId && m.GuildId == guildId))
            .OrderBy(v => v.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("message", vote.MessageId);
            json.WriteNumber("answerId", vote.AnswerId);
            json.WriteString("addedAt", vote.AddedAt);
            json.WriteTime("removedAt", vote.RemovedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
