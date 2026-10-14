using System.Text.Json;

namespace THOBOTTO.Archive;

// The counts an export ends with, for export.json and the status line.
public sealed class ExportCounts
{
    public int Channels { get; set; }

    public int ChannelsWithMessages { get; set; }

    // With messages, but not in the server snapshot: gone before one was taken, or none was.
    public int ChannelsNotInSnapshot { get; set; }

    public int Messages { get; set; }

    public int Deleted { get; set; }

    public int Purged { get; set; }

    public int Versions { get; set; }

    public int Reactions { get; set; }

    public int PollVotes { get; set; }

    public int Attachments { get; set; }

    public int AttachmentFiles { get; set; }

    public int AttachmentsWithoutFile { get; set; }

    public int Members { get; set; }

    public int MembersLeft { get; set; }

    public int Roles { get; set; }

    public int Assets { get; set; }

    public int AssetsWithoutFile { get; set; }

    public int Files { get; set; }
}

// What export.json says: when, which server as the snapshot knew it, what was counted, what to know, which files.
public sealed record ExportManifest(
    DateTimeOffset TakenAt,
    ulong GuildId,
    string Name,
    string? Icon,
    string? Guild,
    IReadOnlyList<GuildSnapshot> Snapshots,
    ExportCounts Counts,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Bot);

// The export's own format, thobotto-export/1: everything the archive holds, as it holds it, with Discord's own JSON
// beside each row. export.json says what the export is; the server's files are arrays of its rows;
// messages/<channel id>.jsonl holds one message per line, whole: versions, attachments, reactions, votes.
public static class ThobottoWriter
{
    public const string Format = "thobotto-export/1";

    public static void WriteMessage(Utf8JsonWriter json, ArchivedMessage message, IReadOnlyList<MessageVersion> versions, IReadOnlyList<ArchivedAttachment> attachments,
        IReadOnlyList<ArchivedReaction> reactions, IReadOnlyList<ArchivedPollVote> votes, Func<ArchivedAttachment, string?> attachmentFile)
    {
        json.WriteStartObject();
        json.WriteId("id", message.Id);
        json.WriteId("channelId", message.ChannelId);
        json.WriteId("authorId", message.AuthorId);
        json.WriteString("createdAt", message.CreatedAt);
        json.WriteTime("editedAt", message.EditedAt);
        json.WriteString("content", message.Content);
        json.WriteTime("deletedAt", message.DeletedAt);
        json.WriteId("deletedById", message.DeletedById);
        json.WriteTime("purgedAt", message.PurgedAt);
        json.WriteJson("raw", message.Raw);

        json.WriteStartArray("versions");
        foreach (var version in versions.OrderBy(v => v.ReplacedAt))
        {
            json.WriteStartObject();
            json.WriteString("content", version.Content);
            json.WriteString("replacedAt", version.ReplacedAt);
            json.WriteJson("raw", version.Raw);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("attachments");
        foreach (var attachment in attachments)
        {
            json.WriteStartObject();
            json.WriteId("id", attachment.Id);
            json.WriteString("fileName", attachment.FileName);
            json.WriteString("contentType", attachment.ContentType);
            json.WriteNumber("size", attachment.Size);
            json.WriteString("url", attachment.Url);
            json.WriteString("file", attachmentFile(attachment));
            json.WriteTime("storedAt", attachment.StoredAt);
            json.WriteTime("failedAt", attachment.FailedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("reactions");
        foreach (var reaction in reactions.OrderBy(r => r.AddedAt))
        {
            json.WriteStartObject();
            json.WriteId("userId", reaction.UserId);
            json.WriteId("emojiId", reaction.EmojiId);
            json.WriteString("emojiName", reaction.EmojiName);
            json.WriteBoolean("burst", reaction.Burst);
            json.WriteString("addedAt", reaction.AddedAt);
            json.WriteTime("removedAt", reaction.RemovedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("pollVotes");
        foreach (var vote in votes.OrderBy(v => v.AddedAt))
        {
            json.WriteStartObject();
            json.WriteId("userId", vote.UserId);
            json.WriteNumber("answerId", vote.AnswerId);
            json.WriteString("addedAt", vote.AddedAt);
            json.WriteTime("removedAt", vote.RemovedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    public static void WriteChannel(Utf8JsonWriter json, ArchivedChannel channel, string? messagesFile)
    {
        json.WriteStartObject();
        json.WriteId("id", channel.Id);
        json.WriteBoolean("inSnapshot", true);
        json.WriteId("parentId", channel.ParentId);
        json.WriteNumber("type", (int)channel.Type);
        json.WriteBoolean("thread", GuildChannelList.IsThread(channel.Type));
        json.WriteString("name", channel.Name);
        json.WriteString("topic", channel.Topic);
        json.WriteNumber("position", channel.Position);
        json.WriteBoolean("nsfw", channel.Nsfw);
        json.WriteNumber("rateLimit", channel.RateLimit);
        json.WriteNumber("flags", (int)channel.Flags);
        json.WriteJson("availableTags", channel.AvailableTags);
        json.WriteJson("appliedTags", channel.AppliedTags);
        json.WriteId("ownerId", channel.OwnerId);
        json.WriteTime("threadCreatedAt", channel.ThreadCreatedAt);
        json.WriteBoolean("archived", channel.Archived);
        json.WriteBoolean("locked", channel.Locked);
        json.WriteJson("permissionOverwrites", channel.PermissionOverwrites);
        json.WriteString("firstSeenAt", channel.FirstSeenAt);
        json.WriteString("lastSeenAt", channel.LastSeenAt);
        json.WriteTime("deletedAt", channel.DeletedAt);
        json.WriteString("messages", messagesFile);
        json.WriteJson("raw", channel.Raw);
        json.WriteEndObject();
    }

    // A channel with messages that the snapshot never listed: its id, the name the backfill saw, its messages.
    public static void WriteUnknownChannel(Utf8JsonWriter json, ulong channelId, string name, string messagesFile)
    {
        json.WriteStartObject();
        json.WriteId("id", channelId);
        json.WriteBoolean("inSnapshot", false);
        json.WriteString("name", name);
        json.WriteString("messages", messagesFile);
        json.WriteEndObject();
    }

    public static void WriteRole(Utf8JsonWriter json, ArchivedRole role, string? iconFile)
    {
        json.WriteStartObject();
        json.WriteId("id", role.Id);
        json.WriteString("name", role.Name);
        json.WriteNumber("color", role.Color);
        json.WriteNumber("position", role.Position);
        json.WriteString("permissions", role.Permissions.ToString());
        json.WriteBoolean("hoist", role.Hoist);
        json.WriteBoolean("mentionable", role.Mentionable);
        json.WriteString("iconHash", role.IconHash);
        json.WriteString("icon", iconFile);
        json.WriteBoolean("managed", role.Managed);
        json.WriteString("firstSeenAt", role.FirstSeenAt);
        json.WriteString("lastSeenAt", role.LastSeenAt);
        json.WriteTime("deletedAt", role.DeletedAt);
        json.WriteJson("raw", role.Raw);
        json.WriteEndObject();
    }

    public static void WriteMember(Utf8JsonWriter json, ArchivedMember member, string? avatarFile, string? guildAvatarFile)
    {
        json.WriteStartObject();
        json.WriteId("id", member.UserId);
        json.WriteString("username", member.Username);
        json.WriteString("globalName", member.GlobalName);
        json.WriteString("nick", member.Nick);
        json.WriteString("avatarHash", member.AvatarHash);
        json.WriteString("avatar", avatarFile);
        json.WriteString("guildAvatarHash", member.GuildAvatarHash);
        json.WriteString("guildAvatar", guildAvatarFile);
        json.WriteJson("roleIds", member.RoleIds);
        json.WriteTime("joinedAt", member.JoinedAt);
        json.WriteTime("premiumSince", member.PremiumSince);
        json.WriteBoolean("bot", member.Bot);
        json.WriteString("firstSeenAt", member.FirstSeenAt);
        json.WriteString("lastSeenAt", member.LastSeenAt);
        json.WriteTime("leftAt", member.LeftAt);
        json.WriteJson("raw", member.Raw);
        json.WriteEndObject();
    }

    // Discord's objects as a snapshot holds them (emojis, stickers), each with where its image is in the export.
    public static void WriteWithFiles(Utf8JsonWriter json, string? snapshot, Func<ulong, string?> file)
    {
        json.WriteStartArray();
        if (snapshot is not null)
        {
            using var document = JsonDocument.Parse(snapshot);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                json.WriteStartObject();
                foreach (var property in item.EnumerateObject())
                    property.WriteTo(json);
                var id = item.TryGetProperty("id", out var idElement) && ulong.TryParse(idElement.GetString(), out var parsed) ? parsed : 0;
                json.WriteString("file", id == 0 ? null : file(id));
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();
    }

    public static void WriteManifest(Utf8JsonWriter json, ExportManifest manifest)
    {
        json.WriteStartObject();
        json.WriteString("format", Format);
        json.WriteString("takenAt", manifest.TakenAt);
        json.WritePropertyName("server");
        json.WriteStartObject();
        json.WriteId("id", manifest.GuildId);
        json.WriteString("name", manifest.Name);
        json.WriteString("icon", manifest.Icon);
        json.WriteTime("snapshotTakenAt", manifest.Snapshots.FirstOrDefault(s => s.Kind == SnapshotKinds.Guild)?.TakenAt);
        json.WriteEndObject();
        json.WriteJson("guild", manifest.Guild);

        json.WritePropertyName("counts");
        json.WriteStartObject();
        var counts = manifest.Counts;
        json.WriteNumber("channels", counts.Channels);
        json.WriteNumber("channelsWithMessages", counts.ChannelsWithMessages);
        json.WriteNumber("channelsNotInSnapshot", counts.ChannelsNotInSnapshot);
        json.WriteNumber("messages", counts.Messages);
        json.WriteNumber("deleted", counts.Deleted);
        json.WriteNumber("purged", counts.Purged);
        json.WriteNumber("versions", counts.Versions);
        json.WriteNumber("reactions", counts.Reactions);
        json.WriteNumber("pollVotes", counts.PollVotes);
        json.WriteNumber("attachments", counts.Attachments);
        json.WriteNumber("attachmentFiles", counts.AttachmentFiles);
        json.WriteNumber("attachmentsWithoutFile", counts.AttachmentsWithoutFile);
        json.WriteNumber("members", counts.Members);
        json.WriteNumber("membersLeft", counts.MembersLeft);
        json.WriteNumber("roles", counts.Roles);
        json.WriteNumber("assets", counts.Assets);
        json.WriteNumber("assetsWithoutFile", counts.AssetsWithoutFile);
        json.WriteNumber("files", counts.Files);
        json.WriteEndObject();

        // When each part of the server was last taken whole, and what stopped the last try.
        json.WritePropertyName("snapshots");
        json.WriteStartObject();
        foreach (var snapshot in manifest.Snapshots.OrderBy(s => s.Kind))
        {
            json.WritePropertyName(snapshot.Kind);
            json.WriteStartObject();
            json.WriteTime("takenAt", snapshot.TakenAt);
            json.WriteString("triedAt", snapshot.TriedAt);
            json.WriteString("problem", snapshot.Problem);
            json.WriteEndObject();
        }
        json.WriteEndObject();

        json.WriteStartArray("notes");
        foreach (var note in manifest.Notes)
            json.WriteStringValue(note);
        json.WriteEndArray();

        json.WritePropertyName("files");
        json.WriteStartObject();
        json.WriteString("channels", "channels.json");
        json.WriteString("roles", "roles.json");
        json.WriteString("members", "members.json");
        json.WriteString("emojis", "emojis.json");
        json.WriteString("stickers", "stickers.json");
        json.WriteString("bans", "bans.json");
        json.WriteString("webhooks", "webhooks.json");
        json.WriteString("events", "events.json");
        json.WriteString("pins", "pins.json");
        json.WriteString("messages", "messages/<channel id>.jsonl");
        json.WriteString("dce", "dce/");
        json.WriteString("attachments", "attachments/");
        json.WriteString("assets", "assets/");
        json.WriteStartArray("bot");
        foreach (var name in manifest.Bot)
            json.WriteStringValue($"bot/{name}.json");
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }
}
