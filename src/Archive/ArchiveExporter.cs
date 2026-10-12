using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Debugging;

namespace THOBOTTO.Archive;

// Writes a guild's archive to a folder: one DiscordChatExporter-style JSON file per channel or
// thread, and the stored attachments under attachments/, linked by relative path. Meant to be
// run on the server (`dotnet THOBOTTO.dll export <guild id> <folder>`) and later from the panel.
// Discord is asked for today's names and categories; the archive stands on its own when it doesn't answer.
public sealed class ArchiveExporter(RestClient rest, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store, TimeProvider time, ILogger<ArchiveExporter> logger)
{
    private const int Batch = 1000;

    private sealed record ExportedGuild(ulong Id, string Name, string IconUrl);

    public async Task ExportAsync(ulong guildId, string folder, CancellationToken ct)
    {
        // The export's lines are this server's.
        using var _ = logger.BeginScope(DebugLog.Guild(guildId));
        Directory.CreateDirectory(Path.Combine(folder, "attachments"));

        List<ulong> channelIds;
        Dictionary<ulong, string> backfillNames;
        string? backfillName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            channelIds = await db.ArchivedMessages.Where(m => m.GuildId == guildId).Select(m => m.ChannelId).Distinct().ToListAsync(ct);
            backfillNames = await db.BackfillChannels.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.ChannelId, c => c.Name, ct);
            backfillName = await db.BackfillRuns.Where(r => r.GuildId == guildId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        }

        var (live, channels) = await AskDiscordAsync(guildId, ct);
        var guild = live is null
            ? new ExportedGuild(guildId, backfillName ?? $"guild-{guildId}", "")
            : new(live.Id, live.Name, live.IconHash is { } icon ? $"https://cdn.discordapp.com/icons/{live.Id}/{icon}.png" : "");

        foreach (var channelId in channelIds)
        {
            var channel = channels.GetValueOrDefault(channelId);
            var name = channel?.Name ?? backfillNames.GetValueOrDefault(channelId) ?? $"channel-{channelId}";
            var count = await ExportChannelAsync(guild, channelId, channel, name, channels, folder, ct);
            logger.LogInformation("Exported #{Channel}: {Count} messages", name, count);
        }
    }

    // The guild and its channels and threads as Discord has them now; nothing when it doesn't answer
    // (the bot was removed from the server, or the export runs where Discord can't be reached).
    private async Task<(RestGuild? Guild, Dictionary<ulong, IGuildChannel> Channels)> AskDiscordAsync(ulong guildId, CancellationToken ct)
    {
        RestGuild guild;
        IReadOnlyList<IGuildChannel> listed;
        Dictionary<ulong, IGuildChannel> channels;
        try
        {
            guild = await rest.GetGuildAsync(guildId, cancellationToken: ct);
            listed = await rest.GetGuildChannelsAsync(guildId, cancellationToken: ct);
            channels = listed.ToDictionary(c => c.Id);
            foreach (var thread in await rest.GetActiveGuildThreadsAsync(guildId, cancellationToken: ct))
                channels[thread.Id] = thread;
        }
        catch (Exception ex) when (ex is RestException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord doesn't answer ({Reason}); names come from the archive", ex.Message);
            return (null, []);
        }

        // Voice channels have a text chat but no threads.
        foreach (var channel in listed.Where(c => c is (TextGuildChannel and not IVoiceGuildChannel) or ForumGuildChannel))
            await AddArchivedThreadsAsync(channel.Id, channels, ct);
        return (guild, channels);
    }

    private async Task AddArchivedThreadsAsync(ulong channelId, Dictionary<ulong, IGuildChannel> channels, CancellationToken ct)
    {
        try
        {
            await foreach (var thread in rest.GetPublicArchivedGuildThreadsAsync(channelId).WithCancellation(ct))
                channels[thread.Id] = thread;
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No access to this channel's threads, or a channel type without threads.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord doesn't answer ({Reason}); thread names in channel {ChannelId} come from the archive", ex.Message, channelId);
        }
    }

    private async Task<int> ExportChannelAsync(ExportedGuild guild, ulong channelId, IGuildChannel? channel, string name,
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
        writer.WriteString("iconUrl", guild.IconUrl);
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
                .Where(m => m.GuildId == guild.Id && m.ChannelId == channelId && m.Id > after)
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

        await using var source = await store.OpenAsync(attachment.StoredKey!, ct);
        if (source is null)
            return;
        await using var file = File.Create(target);
        await source.CopyToAsync(file, ct);
    }

    // Unique per content, keeping the original extension so files open with the right program.
    private static string FileName(ArchivedAttachment a) => $"{a.StoredKey![..16]}{Path.GetExtension(a.FileName)}";

    private static ulong? ParentId(IGuildChannel channel) => channel switch
    {
        GuildThread thread => thread.ParentId,
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
