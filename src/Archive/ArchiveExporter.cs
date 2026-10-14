using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;

using THOBOTTO.Data;
using THOBOTTO.Debugging;

namespace THOBOTTO.Archive;

// Writes a server's archive to a folder from the database and the attachment store alone, so it works with Discord
// gone: the export's own format (export.json, the server's files, messages/<channel id>.jsonl, assets/, and under
// bot/ what each feature keeps of the server) and DiscordChatExporter's beside it under dce/, both naming the same
// attachments/ files. Run on the server as `dotnet THOBOTTO.dll export <guild id> <folder>`.
public sealed class ArchiveExporter(IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store, IEnumerable<IExportSource> sources, TimeProvider time, ILogger<ArchiveExporter> logger)
{
    private const int Batch = 1000;
    private static readonly JsonWriterOptions Indented = new() { Indented = true };
    private static readonly string[] SnapshotKindsWanted =
        [SnapshotKinds.Guild, SnapshotKinds.Channels, SnapshotKinds.Roles, SnapshotKinds.Members, SnapshotKinds.Emojis, SnapshotKinds.Stickers, SnapshotKinds.Bans, SnapshotKinds.Webhooks, SnapshotKinds.Events, SnapshotKinds.Pins];

    // One export's folder, counts and notes.
    private sealed class Export(ulong guildId, string folder, DateTimeOffset takenAt)
    {
        public ulong GuildId => guildId;

        public string Folder => folder;

        public DateTimeOffset TakenAt => takenAt;

        public ExportCounts Counts { get; } = new();

        public List<string> Notes { get; } = [];

        public string Path(params string[] parts) => System.IO.Path.Combine([folder, .. parts]);
    }

    // The server as the snapshot tables hold it, and where its images went in the export.
    private sealed class Server
    {
        public required string Name { get; init; }

        public string? Icon { get; init; }

        public string? Guild { get; init; }

        public required Dictionary<string, GuildSnapshot> Snapshots { get; init; }

        public required Dictionary<ulong, ArchivedChannel> Channels { get; init; }

        public required Dictionary<ulong, ArchivedRole> Roles { get; init; }

        public required Dictionary<ulong, string> BackfillNames { get; init; }

        public required Dictionary<(string Kind, ulong EntityId, string Hash), string> Assets { get; init; }

        public string? Asset(string kind, ulong entityId, string? hash) => hash is null ? null : Assets.GetValueOrDefault((kind, entityId, hash));

        public string? Snapshot(string kind) => Snapshots.GetValueOrDefault(kind)?.Json;
    }

    public async Task<ExportCounts> ExportAsync(ulong guildId, string folder, CancellationToken ct)
    {
        // The export's lines are this server's.
        using var _ = logger.BeginScope(DebugLog.Guild(guildId));
        var export = new Export(guildId, folder, time.GetUtcNow());
        foreach (var sub in new[] { "messages", "dce", "attachments", "assets" })
            Directory.CreateDirectory(export.Path(sub));

        var server = await LoadServerAsync(export, ct);
        List<ulong> channelIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            channelIds = await db.ArchivedMessages.Where(m => m.GuildId == guildId).Select(m => m.ChannelId).Distinct().OrderBy(id => id).ToListAsync(ct);
        export.Counts.ChannelsWithMessages = channelIds.Count;

        await WriteServerAsync(export, server, channelIds.ToHashSet(), ct);
        foreach (var channelId in channelIds)
            await WriteChannelAsync(export, server, channelId, ct);
        var (bot, botFiles) = await ExportSources.WriteAsync(folder, guildId, sources, ct);
        export.Counts.Files += botFiles;
        await WriteManifestAsync(export, server, bot, ct);
        logger.LogInformation("Export of {GuildId} written to {Folder}: {Files} files, {Messages} messages in {Channels} channels and threads",
            guildId, folder, export.Counts.Files, export.Counts.Messages, export.Counts.ChannelsWithMessages);
        return export.Counts;
    }

    private async Task<Server> LoadServerAsync(Export export, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var guildId = export.GuildId;
        var snapshots = await db.GuildSnapshots.AsNoTracking().Where(s => s.GuildId == guildId && SnapshotKindsWanted.Contains(s.Kind)).ToDictionaryAsync(s => s.Kind, ct);
        var channels = await db.ArchivedChannels.AsNoTracking().Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, ct);
        var roles = await db.ArchivedRoles.AsNoTracking().Where(r => r.GuildId == guildId).ToDictionaryAsync(r => r.Id, ct);
        var backfillNames = await db.BackfillChannels.AsNoTracking().Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.ChannelId, c => c.Name, ct);
        var backfillName = await db.BackfillRuns.AsNoTracking().Where(r => r.GuildId == guildId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        var assets = await CopyAssetsAsync(export, await db.ArchivedAssets.AsNoTracking().Where(a => a.GuildId == guildId).OrderBy(a => a.Id).ToListAsync(ct), ct);
        export.Counts.Channels = channels.Count;
        export.Counts.Roles = roles.Count;

        var guild = snapshots.GetValueOrDefault(SnapshotKinds.Guild)?.Json;
        string? name = null;
        string? iconHash = null;
        if (guild is not null)
        {
            using var document = JsonDocument.Parse(guild);
            name = document.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            iconHash = document.RootElement.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null;
        }
        return new Server
        {
            Name = name ?? backfillName ?? $"guild-{guildId}",
            Icon = iconHash is null ? null : assets.GetValueOrDefault((AssetKinds.Icon, guildId, iconHash)),
            Guild = guild,
            Snapshots = snapshots,
            Channels = channels,
            Roles = roles,
            BackfillNames = backfillNames,
            Assets = assets,
        };
    }

    // The server's images into assets/<kind>/, named by what they belong to and the image's hash.
    private async Task<Dictionary<(string, ulong, string), string>> CopyAssetsAsync(Export export, List<ArchivedAsset> assets, CancellationToken ct)
    {
        var copied = new Dictionary<(string, ulong, string), string>();
        foreach (var asset in assets)
        {
            var relative = $"assets/{asset.Kind}/{asset.EntityId}{(asset.Hash.Length == 0 ? "" : "-" + asset.Hash)}{Extension(asset)}";
            if (asset.StoredKey is not null && await CopyAsync(asset.StoredKey, export.Path(relative), ct))
            {
                copied[(asset.Kind, asset.EntityId, asset.Hash)] = relative;
                export.Counts.Assets++;
            }
            else
                export.Counts.AssetsWithoutFile++;
        }
        export.Counts.Files += copied.Count;
        return copied;
    }

    private async Task WriteServerAsync(Export export, Server server, HashSet<ulong> withMessages, CancellationToken ct)
    {
        var guildId = export.GuildId;
        await WriteFileAsync(export, "channels.json", json =>
        {
            json.WriteStartArray();
            foreach (var channel in server.Channels.Values.OrderBy(c => c.ParentId ?? 0).ThenBy(c => c.Position ?? 0).ThenBy(c => c.Id))
                ThobottoWriter.WriteChannel(json, channel, withMessages.Contains(channel.Id) ? MessagesFile(channel.Id) : null);
            foreach (var channelId in withMessages.Where(id => !server.Channels.ContainsKey(id)).Order())
                ThobottoWriter.WriteUnknownChannel(json, channelId, server.BackfillNames.GetValueOrDefault(channelId) ?? $"channel-{channelId}", MessagesFile(channelId));
            json.WriteEndArray();
        }, ct);
        await WriteFileAsync(export, "roles.json", json =>
        {
            json.WriteStartArray();
            foreach (var role in server.Roles.Values.OrderByDescending(r => r.Position).ThenBy(r => r.Id))
                ThobottoWriter.WriteRole(json, role, server.Asset(AssetKinds.RoleIcon, role.Id, role.IconHash));
            json.WriteEndArray();
        }, ct);
        await WriteFileAsync(export, "members.json", async json =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            json.WriteStartArray();
            await foreach (var member in db.ArchivedMembers.AsNoTracking().Where(m => m.GuildId == guildId).OrderBy(m => m.UserId).AsAsyncEnumerable().WithCancellation(ct))
            {
                ThobottoWriter.WriteMember(json, member, server.Asset(AssetKinds.Avatar, member.UserId, member.AvatarHash), server.Asset(AssetKinds.Avatar, member.UserId, member.GuildAvatarHash));
                export.Counts.Members++;
                if (member.LeftAt is not null)
                    export.Counts.MembersLeft++;
                if (export.Counts.Members % Batch == 0)
                    await json.FlushAsync(ct);
            }
            json.WriteEndArray();
        }, ct);
        await WriteFileAsync(export, "emojis.json", json => ThobottoWriter.WriteWithFiles(json, server.Snapshot(SnapshotKinds.Emojis), id => server.Asset(AssetKinds.Emoji, id, "")), ct);
        await WriteFileAsync(export, "stickers.json", json => ThobottoWriter.WriteWithFiles(json, server.Snapshot(SnapshotKinds.Stickers), id => server.Asset(AssetKinds.Sticker, id, "")), ct);
        foreach (var (kind, empty) in new[] { (SnapshotKinds.Bans, "[]"), (SnapshotKinds.Webhooks, "[]"), (SnapshotKinds.Events, "[]"), (SnapshotKinds.Pins, "{}") })
            await WriteFileAsync(export, $"{kind}.json", json => json.WriteJsonValue(server.Snapshot(kind) ?? empty), ct);
    }

    private async Task WriteChannelAsync(Export export, Server server, ulong channelId, CancellationToken ct)
    {
        var channel = server.Channels.GetValueOrDefault(channelId) ?? Unknown(export, server, channelId);
        var parent = channel.ParentId is { } parentId ? server.Channels.GetValueOrDefault(parentId) : null;
        var counts = export.Counts;

        await using var lines = File.Create(export.Path("messages", $"{channelId}.jsonl"));
        await using var line = new Utf8JsonWriter(lines);
        await using var dceFile = File.Create(export.Path("dce", $"{ExportFolder.Safe(parent?.Name)}{(parent is null ? "" : " - ")}{ExportFolder.Safe(channel.Name)} [{channelId}].json"));
        await using var dce = new Utf8JsonWriter(dceFile, Indented);
        DceWriter.WriteStart(dce, export.GuildId, server.Name, server.Icon ?? "", channel, parent, export.TakenAt);

        var count = 0;
        ulong after = 0;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var messages = await db.ArchivedMessages.AsNoTracking()
                .Where(m => m.GuildId == export.GuildId && m.ChannelId == channelId && m.Id > after)
                .OrderBy(m => m.Id)
                .Take(Batch)
                .ToListAsync(ct);
            if (messages.Count == 0)
                break;

            var ids = messages.Select(m => m.Id).ToList();
            var versions = (await db.MessageVersions.AsNoTracking().Where(v => ids.Contains(v.MessageId)).ToListAsync(ct)).ToLookup(v => v.MessageId);
            var attachments = (await db.ArchivedAttachments.AsNoTracking().Where(a => ids.Contains(a.MessageId)).ToListAsync(ct)).ToLookup(a => a.MessageId);
            var reactions = (await db.ArchivedReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId)).ToListAsync(ct)).ToLookup(r => r.MessageId);
            var votes = (await db.ArchivedPollVotes.AsNoTracking().Where(v => ids.Contains(v.MessageId)).ToListAsync(ct)).ToLookup(v => v.MessageId);
            var authorIds = messages.Select(m => m.AuthorId).Distinct().ToList();
            var members = await db.ArchivedMembers.AsNoTracking().Where(m => m.GuildId == export.GuildId && authorIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, ct);
            var lookups = new ExportLookups(id => members.GetValueOrDefault(id), id => server.Roles.GetValueOrDefault(id), server.Asset);

            foreach (var message in messages)
            {
                var files = attachments[message.Id].ToList();
                var paths = new Dictionary<ulong, string>();
                foreach (var stored in files.Where(f => f.StoredKey is not null))
                {
                    if (await CopyAttachmentAsync(export, stored, ct) is { } path)
                        paths[stored.Id] = path;
                }
                var messageVersions = versions[message.Id].ToList();
                var messageReactions = reactions[message.Id].ToList();
                var messageVotes = votes[message.Id].ToList();

                ThobottoWriter.WriteMessage(line, message, messageVersions, files, messageReactions, messageVotes, a => paths.GetValueOrDefault(a.Id));
                await line.FlushAsync(ct);
                lines.WriteByte((byte)'\n');
                line.Reset();
                DceWriter.WriteMessage(dce, message, messageVersions, files, messageReactions, messageVotes, a => paths.GetValueOrDefault(a.Id) ?? a.Url, lookups);

                count++;
                counts.Messages++;
                counts.Versions += messageVersions.Count;
                counts.Reactions += messageReactions.Count;
                counts.PollVotes += messageVotes.Count;
                counts.Attachments += files.Count;
                counts.AttachmentFiles += paths.Count;
                counts.AttachmentsWithoutFile += files.Count - paths.Count;
                if (message.DeletedAt is not null)
                    counts.Deleted++;
                if (message.PurgedAt is not null)
                    counts.Purged++;
            }
            await dce.FlushAsync(ct);
            after = messages[^1].Id;
        }
        DceWriter.WriteEnd(dce, count);
        counts.Files += 2;
        logger.LogInformation("Exported #{Channel}: {Count} messages", channel.Name, count);
    }

    // A channel the snapshot never listed, named as the backfill saw it.
    private static ArchivedChannel Unknown(Export export, Server server, ulong channelId)
    {
        export.Counts.ChannelsNotInSnapshot++;
        return new()
        {
            Id = channelId,
            GuildId = export.GuildId,
            Type = ChannelType.TextGuildChannel,
            Name = server.BackfillNames.GetValueOrDefault(channelId) ?? $"channel-{channelId}",
            Raw = "{}",
        };
    }

    private async Task WriteManifestAsync(Export export, Server server, IReadOnlyList<string> bot, CancellationToken ct)
    {
        var counts = export.Counts;
        var notes = export.Notes;
        var never = SnapshotKindsWanted.Where(kind => !server.Snapshots.ContainsKey(kind)).ToList();
        if (never.Count == SnapshotKindsWanted.Length)
            notes.Add($"No snapshot of the server was ever taken: it's named {server.Name} from the backfill or by its id, channels are named as the backfill saw them, "
                + "and roles.json, members.json, emojis.json, stickers.json, bans.json, webhooks.json, events.json and pins.json are empty. Run /archive snapshot while the server exists.");
        else if (never.Count > 0)
            notes.Add($"Never taken: {string.Join(", ", never)}; their files are empty. Run /archive snapshot while the server exists.");
        foreach (var snapshot in server.Snapshots.Values.Where(s => s.Problem is not null).OrderBy(s => s.Kind))
        {
            notes.Add(snapshot.TakenAt is { } taken
                ? $"The {snapshot.Kind} snapshot's last try failed ({snapshot.Problem}); the one from {taken:u} is used."
                : $"The {snapshot.Kind} snapshot couldn't be taken: {snapshot.Problem}.");
        }
        if (counts.ChannelsNotInSnapshot > 0)
            notes.Add($"{counts.ChannelsNotInSnapshot} channels or threads with messages aren't in the server snapshot (gone before it was taken): named as the backfill saw them, or channel-<id>.");
        if (!store.Enabled)
            notes.Add("Attachment files and the server's images aren't stored for this bot (Archive:Storage is none): the export has Discord's links alone, which expire.");
        if (counts.AttachmentsWithoutFile > 0)
            notes.Add($"{counts.AttachmentsWithoutFile} of {counts.Attachments} attachments have no stored file; their Discord links are kept but expire.");
        if (counts.AssetsWithoutFile > 0)
            notes.Add($"{counts.AssetsWithoutFile} images of the server and its members (emojis, stickers, icons, avatars) couldn't be fetched or copied; their Discord links are in the JSON.");
        if (counts.Purged > 0)
            notes.Add($"{counts.Purged} messages were purged: their text and JSON are gone for good; who, where and when remain.");

        if (notes.Count > 0)
        {
            await File.WriteAllLinesAsync(export.Path("notes.txt"), notes, ct);
            counts.Files++;
        }
        // Counted before it's written, so it counts itself.
        counts.Files++;
        await WriteFileAsync(export, "export.json", json => ThobottoWriter.WriteManifest(json, new(export.TakenAt, export.GuildId, server.Name, server.Icon, server.Guild, [.. server.Snapshots.Values], counts, notes, bot)), ct, counted: false);
    }

    private static Task WriteFileAsync(Export export, string name, Action<Utf8JsonWriter> write, CancellationToken ct, bool counted = true)
        => WriteFileAsync(export, name, json =>
        {
            write(json);
            return Task.CompletedTask;
        }, ct, counted);

    private static async Task WriteFileAsync(Export export, string name, Func<Utf8JsonWriter, Task> write, CancellationToken ct, bool counted = true)
    {
        await using var stream = File.Create(export.Path(name));
        await using var json = new Utf8JsonWriter(stream, Indented);
        await write(json);
        await json.FlushAsync(ct);
        if (counted)
            export.Counts.Files++;
    }

    // The attachment's file in attachments/, named by its name and content; null when the store doesn't have it.
    private async Task<string?> CopyAttachmentAsync(Export export, ArchivedAttachment attachment, CancellationToken ct)
    {
        var relative = $"attachments/{AttachmentFileName(attachment)}";
        var target = export.Path(relative);
        if (File.Exists(target))
            return relative;
        if (!await CopyAsync(attachment.StoredKey!, target, ct))
            return null;
        export.Counts.Files++;
        return relative;
    }

    private async Task<bool> CopyAsync(string key, string target, CancellationToken ct)
    {
        if (File.Exists(target))
            return true;
        await using var source = await store.OpenAsync(key, ct);
        if (source is null)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var file = File.Create(target);
        await source.CopyToAsync(file, ct);
        return true;
    }

    private static string MessagesFile(ulong channelId) => $"messages/{channelId}.jsonl";

    // The original name, then the content's hash, so the same name posted twice keeps both files and a reader still
    // knows what each is.
    public static string AttachmentFileName(ArchivedAttachment attachment)
    {
        var name = ExportFolder.Safe(Path.GetFileNameWithoutExtension(attachment.FileName));
        return $"{(name.Length == 0 ? "file" : name)}-{attachment.StoredKey![..16]}{Path.GetExtension(attachment.FileName)}";
    }

    private static string Extension(ArchivedAsset asset) => asset.ContentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "application/json" => ".json",
        _ => Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) ? Path.GetExtension(url.AbsolutePath) : "",
    };
}
