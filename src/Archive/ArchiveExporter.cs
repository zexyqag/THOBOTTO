using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;

namespace THOBOTTO.Archive;

// Writes a guild's archive to a folder: one DiscordChatExporter-style JSON file per channel or
// thread, and the stored attachments under attachments/, linked by relative path. Meant to be
// run on the server (`dotnet THOBOTTO.dll export <guild id> <folder>`) and later from the panel.
public sealed class ArchiveExporter(RestClient rest, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store, TimeProvider time, ILogger<ArchiveExporter> logger)
{
    private const int Batch = 1000;

    public async Task ExportAsync(ulong guildId, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(folder, "attachments"));
        var guild = await rest.GetGuildAsync(guildId, cancellationToken: ct);
        var channels = (await rest.GetGuildChannelsAsync(guildId, cancellationToken: ct)).ToDictionary(c => c.Id);

        List<ulong> channelIds;
        Dictionary<ulong, string> backfillNames;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            channelIds = await db.ArchivedMessages.Where(m => m.GuildId == guildId).Select(m => m.ChannelId).Distinct().ToListAsync(ct);
            backfillNames = await db.BackfillChannels.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.ChannelId, c => c.Name, ct);
        }

        foreach (var channelId in channelIds)
        {
            var channel = channels.GetValueOrDefault(channelId);
            var name = channel?.Name ?? backfillNames.GetValueOrDefault(channelId) ?? $"channel-{channelId}";
            var count = await ExportChannelAsync(guild, channelId, channel, name, channels, folder, ct);
            logger.LogInformation("Exported #{Channel}: {Count} messages", name, count);
        }
    }

    private async Task<int> ExportChannelAsync(RestGuild guild, ulong channelId, IGuildChannel? channel, string name,
        Dictionary<ulong, IGuildChannel> channels, string folder, CancellationToken ct)
    {
        var category = channel is not null && ParentId(channel) is { } parentId ? channels.GetValueOrDefault(parentId) : null;
        var file = Path.Combine(folder, $"{Safe(category?.Name)}{(category is null ? "" : " - ")}{Safe(name)} [{channelId}].json");

        await using var stream = File.Create(file);
        await using var writer = new Utf8JsonWriter(stream, new() { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName("guild");
        writer.WriteStartObject();
        writer.WriteString("id", guild.Id.ToString());
        writer.WriteString("name", guild.Name);
        writer.WriteString("iconUrl", guild.IconHash is { } icon ? $"https://cdn.discordapp.com/icons/{guild.Id}/{icon}.png" : "");
        writer.WriteEndObject();

        writer.WritePropertyName("channel");
        writer.WriteStartObject();
        writer.WriteString("id", channelId.ToString());
        writer.WriteString("type", Kind(channel));
        writer.WriteString("categoryId", category?.Id.ToString() ?? "");
        writer.WriteString("category", category?.Name ?? "");
        writer.WriteString("name", name);
        writer.WriteString("topic", channel is TextGuildChannel text ? text.Topic : null);
        writer.WriteEndObject();

        writer.WritePropertyName("dateRange");
        writer.WriteStartObject();
        writer.WriteNull("after");
        writer.WriteNull("before");
        writer.WriteEndObject();
        writer.WriteString("exportedAt", time.GetUtcNow());

        writer.WriteStartArray("messages");
        var count = 0;
        ulong after = 0;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var messages = await db.ArchivedMessages
                .Where(m => m.ChannelId == channelId && m.Id > after)
                .OrderBy(m => m.Id)
                .Take(Batch)
                .ToListAsync(ct);
            if (messages.Count == 0)
                break;

            var ids = messages.Select(m => m.Id).ToList();
            var versions = (await db.MessageVersions.Where(v => ids.Contains(v.MessageId)).ToListAsync(ct)).ToLookup(v => v.MessageId);
            var attachments = (await db.ArchivedAttachments.Where(a => ids.Contains(a.MessageId)).ToListAsync(ct)).ToLookup(a => a.MessageId);

            foreach (var message in messages)
            {
                var files = attachments[message.Id].ToList();
                foreach (var stored in files.Where(f => f.StoredKey is not null))
                    await CopyAsync(stored, folder, ct);

                DceWriter.WriteMessage(writer, message, versions[message.Id].ToList(), files,
                    a => a.StoredKey is null ? a.Url : $"attachments/{FileName(a)}");
                count++;
            }

            await writer.FlushAsync(ct);
            after = messages[^1].Id;
        }
        writer.WriteEndArray();
        writer.WriteNumber("messageCount", count);
        writer.WriteEndObject();
        return count;
    }

    private async Task CopyAsync(ArchivedAttachment attachment, string folder, CancellationToken ct)
    {
        var target = Path.Combine(folder, "attachments", FileName(attachment));
        if (File.Exists(target))
            return;

        await using var source = store.Open(attachment.StoredKey!);
        if (source is null)
            return;
        await using var file = File.Create(target);
        await source.CopyToAsync(file, ct);
    }

    // Unique per content, keeping the original extension so files open with the right program.
    private static string FileName(ArchivedAttachment a) => $"{a.StoredKey![..16]}{Path.GetExtension(a.FileName)}";

    private static ulong? ParentId(IGuildChannel channel) => channel switch
    {
        TextGuildChannel text => text.ParentId,
        ForumGuildChannel forum => forum.ParentId,
        _ => null,
    };

    private static string Kind(IGuildChannel? channel) => channel switch
    {
        PublicGuildThread => "GuildPublicThread",
        PrivateGuildThread => "GuildPrivateThread",
        AnnouncementGuildThread => "GuildNewsThread",
        IVoiceGuildChannel => "GuildVoiceChat",
        AnnouncementGuildChannel => "GuildNewsChat",
        _ => "GuildTextChat",
    };

    private static string Safe(string? name)
        => name is null ? "" : string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
