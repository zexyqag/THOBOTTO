using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Expressions;

// Every member-made emoji and sticker of the server, from proposal to retirement, with its votes, and the images
// under assets/expressions/ (a retired one's image exists nowhere else), for the archive's export.
public sealed class ExpressionsExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource, IExportFiles
{
    public string Name => "expressions";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var votes = (await db.ExpressionVotes.AsNoTracking().Where(v => db.Expressions.Any(e => e.GuildId == guildId && e.Id == v.ExpressionId)).ToListAsync()).ToLookup(v => v.ExpressionId);
        json.WriteStartArray();
        // The image stays in the database until the files are written.
        var expressions = db.Expressions.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.Id).Select(e => new
        {
            e.Id,
            e.Kind,
            e.Name,
            e.Tag,
            e.CreatorId,
            e.ProposerId,
            e.RevivedFromId,
            e.ContentType,
            e.Animated,
            e.State,
            e.Paid,
            e.VoteChannelId,
            e.VoteMessageId,
            e.ProposedAt,
            e.VoteEndsAt,
            e.DecidedAt,
            e.DiscordId,
            e.LiveAt,
            e.LastUsedAt,
            e.Uses,
            e.RetiredAt,
        });
        await foreach (var expression in expressions.AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", expression.Id);
            json.WriteString("kind", expression.Kind);
            json.WriteString("name", expression.Name);
            json.WriteString("tag", expression.Tag);
            json.WriteId("creator", expression.CreatorId);
            json.WriteId("proposer", expression.ProposerId);
            json.WriteNumber("revivedFrom", expression.RevivedFromId);
            json.WriteString("contentType", expression.ContentType);
            json.WriteString("file", ExportFolder.PathOf(Name, FileName(expression.Id, expression.Name, expression.ContentType)));
            json.WriteBoolean("animated", expression.Animated);
            json.WriteString("state", expression.State);
            json.WriteNumber("paid", expression.Paid);
            json.WriteId("voteChannel", expression.VoteChannelId);
            json.WriteId("voteMessage", expression.VoteMessageId);
            json.WriteString("proposedAt", expression.ProposedAt);
            json.WriteString("voteEndsAt", expression.VoteEndsAt);
            json.WriteTime("decidedAt", expression.DecidedAt);
            json.WriteId("discordId", expression.DiscordId);
            json.WriteTime("liveAt", expression.LiveAt);
            json.WriteTime("lastUsedAt", expression.LastUsedAt);
            json.WriteNumber("uses", expression.Uses);
            json.WriteTime("retiredAt", expression.RetiredAt);
            json.WriteStartArray("votes");
            foreach (var vote in votes[expression.Id])
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
        await foreach (var expression in db.Expressions.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.Id).Select(e => new { e.Id, e.Name, e.ContentType, e.Image }).AsAsyncEnumerable())
            await folder.SaveAsync(FileName(expression.Id, expression.Name, expression.ContentType), expression.Image);
    }

    private static string FileName(long id, string name, string contentType) => $"{id}-{ExportFolder.Safe(name)}{Extension(contentType)}";

    private static string Extension(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "application/json" => ".json",
        _ => "",
    };
}
