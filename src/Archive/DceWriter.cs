using System.Text.Json;

using NetCord;

namespace THOBOTTO.Archive;

// Writes archived messages in DiscordChatExporter's JSON format, which migration tools for other platforms read.
// What DCE has no field for (deletions, earlier versions, purges, forwards, polls, components, the number of a
// message's type) goes in extra fields that importers ignore. A message is taken from Discord's raw JSON of it;
// who reacted and who voted from the archive's rows; a nickname, roles and avatar the message doesn't carry from
// the member as the archive last saw them; images from the export's assets where they were kept.
public static class DceWriter
{
    // Discord's message types, named as DCE names the ones it knows; the rest in the same style.
    private static readonly Dictionary<int, string> Kinds = new()
    {
        [0] = "Default",
        [1] = "RecipientAdd",
        [2] = "RecipientRemove",
        [3] = "Call",
        [4] = "ChannelNameChange",
        [5] = "ChannelIconChange",
        [6] = "ChannelPinnedMessage",
        [7] = "GuildMemberJoin",
        [8] = "GuildBoost",
        [9] = "GuildBoostTier1",
        [10] = "GuildBoostTier2",
        [11] = "GuildBoostTier3",
        [12] = "ChannelFollowAdd",
        [14] = "GuildDiscoveryDisqualified",
        [15] = "GuildDiscoveryRequalified",
        [16] = "GuildDiscoveryGracePeriodInitialWarning",
        [17] = "GuildDiscoveryGracePeriodFinalWarning",
        [18] = "ThreadCreated",
        [19] = "Reply",
        [20] = "ChatInputCommand",
        [21] = "ThreadStarterMessage",
        [22] = "GuildInviteReminder",
        [23] = "ContextMenuCommand",
        [24] = "AutoModerationAction",
        [25] = "RoleSubscriptionPurchase",
        [26] = "InteractionPremiumUpsell",
        [27] = "StageStart",
        [28] = "StageEnd",
        [29] = "StageSpeaker",
        [31] = "StageTopic",
        [32] = "GuildApplicationPremiumSubscription",
        [36] = "GuildIncidentAlertModeEnabled",
        [37] = "GuildIncidentAlertModeDisabled",
        [38] = "GuildIncidentReportRaid",
        [39] = "GuildIncidentReportFalseAlarm",
        [44] = "PurchaseNotification",
        [46] = "PollResult",
    };

    // A file's head: the server, the channel (a thread's "category" is its channel, as DCE has it) and the
    // messages array, left open for WriteEnd.
    public static void WriteStart(Utf8JsonWriter writer, ulong guildId, string guildName, string iconUrl, ArchivedChannel channel, ArchivedChannel? parent, DateTimeOffset exportedAt)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("guild");
        writer.WriteStartObject();
        writer.WriteString("id", guildId.ToString());
        writer.WriteString("name", guildName);
        writer.WriteString("iconUrl", iconUrl);
        writer.WriteEndObject();

        writer.WritePropertyName("channel");
        writer.WriteStartObject();
        writer.WriteString("id", channel.Id.ToString());
        writer.WriteString("type", Kind(channel.Type));
        writer.WriteString("categoryId", parent?.Id.ToString() ?? "");
        writer.WriteString("category", parent?.Name ?? "");
        writer.WriteString("name", channel.Name);
        writer.WriteString("topic", channel.Topic);
        writer.WriteEndObject();

        writer.WritePropertyName("dateRange");
        writer.WriteStartObject();
        writer.WriteNull("after");
        writer.WriteNull("before");
        writer.WriteEndObject();
        writer.WriteString("exportedAt", exportedAt);
        writer.WriteStartArray("messages");
    }

    public static void WriteEnd(Utf8JsonWriter writer, int messageCount)
    {
        writer.WriteEndArray();
        writer.WriteNumber("messageCount", messageCount);
        writer.WriteEndObject();
    }

    // DCE's ChannelKind names, Discord's numbers.
    public static string Kind(ChannelType type) => type switch
    {
        ChannelType.VoiceGuildChannel => "GuildVoiceChat",
        ChannelType.CategoryChannel => "GuildCategory",
        ChannelType.AnnouncementGuildChannel => "GuildNews",
        ChannelType.AnnouncementGuildThread => "GuildNewsThread",
        ChannelType.PublicGuildThread => "GuildPublicThread",
        ChannelType.PrivateGuildThread => "GuildPrivateThread",
        ChannelType.StageGuildChannel => "GuildStageVoice",
        ChannelType.DirectoryGuildChannel => "GuildDirectory",
        ChannelType.ForumGuildChannel => "GuildForum",
        ChannelType.MediaForumGuildChannel => "GuildMedia",
        _ => "GuildTextChat",
    };

    public static void WriteMessage(
        Utf8JsonWriter writer,
        ArchivedMessage message,
        IReadOnlyList<MessageVersion> versions,
        IReadOnlyList<ArchivedAttachment> attachments,
        IReadOnlyList<ArchivedReaction> reactions,
        IReadOnlyList<ArchivedPollVote> votes,
        Func<ArchivedAttachment, string> attachmentUrl,
        ExportLookups lookups)
    {
        using var raw = message.Raw is null ? null : JsonDocument.Parse(message.Raw);
        var root = raw?.RootElement;
        var type = Int(root, "type") ?? 0;

        writer.WriteStartObject();
        writer.WriteString("id", message.Id.ToString());
        writer.WriteString("type", Kinds.GetValueOrDefault(type) ?? type.ToString());
        writer.WriteNumber("rawType", type);
        writer.WriteString("timestamp", message.CreatedAt);
        Timestamp(writer, "timestampEdited", message.EditedAt);
        writer.WriteString("callEndedTimestamp", root is { } withCall && withCall.TryGetProperty("call", out var call) && call.ValueKind == JsonValueKind.Object ? String(call, "ended_timestamp") : null);
        writer.WriteBoolean("isPinned", root?.TryGetProperty("pinned", out var pinned) == true && pinned.ValueKind == JsonValueKind.True);
        writer.WriteString("content", message.Content ?? "");

        writer.WritePropertyName("author");
        WriteUser(writer, Object(root, "author"), message.AuthorId, Object(root, "member"), lookups);

        var rawAttachments = new Dictionary<string, JsonElement>();
        foreach (var attachment in Array(root, "attachments"))
            rawAttachments.TryAdd(String(attachment, "id") ?? "", attachment);
        writer.WriteStartArray("attachments");
        foreach (var attachment in attachments)
            WriteAttachment(writer, attachment.Id.ToString(), attachmentUrl(attachment), attachment.FileName, attachment.Size, rawAttachments.GetValueOrDefault(attachment.Id.ToString()));
        writer.WriteEndArray();

        writer.WriteStartArray("embeds");
        foreach (var embed in Array(root, "embeds"))
            WriteEmbed(writer, embed);
        writer.WriteEndArray();

        WriteStickers(writer, root, lookups);
        WriteReactions(writer, root, reactions, lookups);

        writer.WriteStartArray("mentions");
        foreach (var mentioned in Array(root, "mentions"))
        {
            if (ulong.TryParse(String(mentioned, "id"), out var id))
                WriteUser(writer, mentioned, id, Object(mentioned, "member"), lookups);
        }
        writer.WriteEndArray();

        if (Object(root, "message_reference") is { } reference)
        {
            writer.WritePropertyName("reference");
            writer.WriteStartObject();
            writer.WriteString("messageId", String(reference, "message_id"));
            writer.WriteString("channelId", String(reference, "channel_id"));
            writer.WriteString("guildId", String(reference, "guild_id"));
            writer.WriteEndObject();
        }

        // The slash command or context menu command this message answered.
        if ((Object(root, "interaction") ?? Object(root, "interaction_metadata")) is { } interaction)
        {
            writer.WritePropertyName("interaction");
            writer.WriteStartObject();
            writer.WriteString("id", String(interaction, "id"));
            writer.WriteString("name", String(interaction, "name"));
            writer.WritePropertyName("user");
            var user = Object(interaction, "user");
            if (user is { } u && ulong.TryParse(String(u, "id"), out var userId))
                WriteUser(writer, u, userId, null, lookups);
            else
                writer.WriteNullValue();
            writer.WriteEndObject();
        }

        writer.WriteStartArray("inlineEmojis");
        writer.WriteEndArray();

        // Not in DCE's format; importers skip unknown fields.
        WriteForwarded(writer, root, lookups);
        WritePoll(writer, root, votes);
        if (root?.TryGetProperty("components", out var components) == true)
        {
            writer.WritePropertyName("components");
            components.WriteTo(writer);
        }
        writer.WriteBoolean("isDeleted", message.DeletedAt is not null);
        Timestamp(writer, "deletedTimestamp", message.DeletedAt);
        writer.WriteString("deletedById", message.DeletedById?.ToString());
        writer.WriteBoolean("isPurged", message.PurgedAt is not null);
        writer.WriteStartArray("edits");
        foreach (var version in versions.OrderBy(v => v.ReplacedAt))
        {
            writer.WriteStartObject();
            writer.WriteString("content", version.Content ?? "");
            writer.WriteString("replacedTimestamp", version.ReplacedAt);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    // The forwarded messages (Discord's message_snapshots), as far as a snapshot carries them.
    private static void WriteForwarded(Utf8JsonWriter writer, JsonElement? root, ExportLookups lookups)
    {
        writer.WriteStartArray("forwarded");
        foreach (var snapshot in Array(root, "message_snapshots"))
        {
            if (Object(snapshot, "message") is not { } forwarded)
                continue;
            var type = Int(forwarded, "type") ?? 0;
            writer.WriteStartObject();
            writer.WriteString("type", Kinds.GetValueOrDefault(type) ?? type.ToString());
            writer.WriteString("content", String(forwarded, "content") ?? "");
            writer.WriteString("timestamp", String(forwarded, "timestamp"));
            writer.WriteString("timestampEdited", String(forwarded, "edited_timestamp"));
            writer.WriteStartArray("attachments");
            foreach (var attachment in Array(forwarded, "attachments"))
                WriteAttachment(writer, String(attachment, "id") ?? "", String(attachment, "url") ?? "", String(attachment, "filename") ?? "", Long(attachment, "size") ?? 0, attachment);
            writer.WriteEndArray();
            writer.WriteStartArray("embeds");
            foreach (var embed in Array(forwarded, "embeds"))
                WriteEmbed(writer, embed);
            writer.WriteEndArray();
            WriteStickers(writer, forwarded, lookups);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    // The poll, its answers with how many and who voted: from the votes the archive saw when it has any, else
    // from the counts Discord last gave.
    private static void WritePoll(Utf8JsonWriter writer, JsonElement? root, IReadOnlyList<ArchivedPollVote> votes)
    {
        if (Object(root, "poll") is not { } poll)
            return;
        var counted = new Dictionary<int, int>();
        var results = Object(poll, "results");
        foreach (var count in Array(results, "answer_counts"))
        {
            if (Int(count, "id") is { } id)
                counted[id] = Int(count, "count") ?? 0;
        }
        var current = votes.Where(v => v.RemovedAt is null).ToLookup(v => v.AnswerId);

        writer.WritePropertyName("poll");
        writer.WriteStartObject();
        writer.WriteString("question", Object(poll, "question") is { } question ? String(question, "text") : null);
        writer.WriteStartArray("answers");
        foreach (var answer in Array(poll, "answers"))
        {
            var id = Int(answer, "answer_id") ?? 0;
            var media = Object(answer, "poll_media");
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("text", media is { } m ? String(m, "text") : null);
            writer.WritePropertyName("emoji");
            if (Object(media, "emoji") is { } emoji)
            {
                writer.WriteStartObject();
                writer.WriteString("id", String(emoji, "id"));
                writer.WriteString("name", String(emoji, "name"));
                writer.WriteEndObject();
            }
            else
                writer.WriteNullValue();
            var voters = current[id].Select(v => v.UserId).ToList();
            writer.WriteNumber("count", votes.Count > 0 ? voters.Count : counted.GetValueOrDefault(id));
            writer.WriteStartArray("voters");
            foreach (var voter in voters)
                writer.WriteStringValue(voter.ToString());
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("expiresAt", String(poll, "expiry"));
        writer.WriteBoolean("allowsMultiselect", poll.TryGetProperty("allow_multiselect", out var multiselect) && multiselect.ValueKind == JsonValueKind.True);
        writer.WriteNumber("layoutType", Int(poll, "layout_type") ?? 1);
        writer.WriteBoolean("isFinalized", results is { } r && r.TryGetProperty("is_finalized", out var finalized) && finalized.ValueKind == JsonValueKind.True);
        writer.WriteEndObject();
    }

    private static void WriteAttachment(Utf8JsonWriter writer, string id, string url, string fileName, long size, JsonElement? raw)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("url", url);
        writer.WriteString("fileName", fileName);
        writer.WriteNumber("fileSizeBytes", size);
        // Beyond DCE's fields: what Discord knew of the file.
        writer.WriteString("contentType", raw is { } c ? String(c, "content_type") : null);
        writer.WriteString("description", raw is { } d ? String(d, "description") : null);
        WriteNumber(writer, "width", Int(raw, "width"));
        WriteNumber(writer, "height", Int(raw, "height"));
        writer.WritePropertyName("durationSecs");
        if (raw is { } withDuration && withDuration.TryGetProperty("duration_secs", out var duration) && duration.ValueKind == JsonValueKind.Number)
            writer.WriteNumberValue(duration.GetDouble());
        else
            writer.WriteNullValue();
        writer.WriteString("waveform", raw is { } w ? String(w, "waveform") : null);
        writer.WriteBoolean("isSpoiler", fileName.StartsWith("SPOILER_", StringComparison.Ordinal));
        writer.WriteEndObject();
    }

    private static void WriteStickers(Utf8JsonWriter writer, JsonElement? root, ExportLookups lookups)
    {
        writer.WriteStartArray("stickers");
        foreach (var sticker in Array(root, "sticker_items"))
        {
            var id = String(sticker, "id") ?? "";
            var (format, extension) = Int(sticker, "format_type") switch
            {
                2 => ("Apng", "png"),
                3 => ("Lottie", "json"),
                4 => ("Gif", "gif"),
                _ => ("Png", "png"),
            };
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("name", String(sticker, "name"));
            writer.WriteString("format", format);
            writer.WriteString("sourceUrl", (ulong.TryParse(id, out var stickerId) ? lookups.Asset(AssetKinds.Sticker, stickerId, "") : null) ?? $"https://media.discordapp.net/stickers/{id}.{extension}");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    // Who reacts with what, from the archive's rows when it has any for the message; else the counts Discord's
    // JSON holds, with nobody named.
    private static void WriteReactions(Utf8JsonWriter writer, JsonElement? root, IReadOnlyList<ArchivedReaction> reactions, ExportLookups lookups)
    {
        var animated = new Dictionary<string, bool>();
        foreach (var reaction in Array(root, "reactions"))
        {
            if (Object(reaction, "emoji") is { } emoji && String(emoji, "id") is { } id)
                animated[id] = emoji.TryGetProperty("animated", out var a) && a.ValueKind == JsonValueKind.True;
        }

        writer.WriteStartArray("reactions");
        if (reactions.Count > 0)
        {
            // A custom emoji is its id, under its latest name (ReactionChanges.SameEmoji); a standard one is its name.
            foreach (var group in reactions.Where(r => r.RemovedAt is null).OrderBy(r => r.AddedAt).GroupBy(r => (r.EmojiId, Name: r.EmojiId is null ? r.EmojiName : "")))
            {
                var emojiId = group.Key.EmojiId;
                var name = group.LastOrDefault(r => r.EmojiName.Length > 0)?.EmojiName ?? "";
                var image = emojiId is { } custom ? lookups.Asset(AssetKinds.Emoji, custom, "") : null;
                var isAnimated = emojiId is { } e && (animated.GetValueOrDefault(e.ToString()) || image?.EndsWith(".gif", StringComparison.Ordinal) == true);
                writer.WriteStartObject();
                WriteEmoji(writer, emojiId?.ToString(), name, isAnimated, image);
                writer.WriteNumber("count", group.Count());
                writer.WriteStartArray("users");
                foreach (var reaction in group)
                    WriteUser(writer, null, reaction.UserId, null, lookups);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }
        else
        {
            foreach (var reaction in Array(root, "reactions"))
            {
                if (Object(reaction, "emoji") is not { } emoji)
                    continue;
                var id = String(emoji, "id");
                writer.WriteStartObject();
                WriteEmoji(writer, id, String(emoji, "name"), id is not null && animated.GetValueOrDefault(id), id is not null && ulong.TryParse(id, out var emojiId) ? lookups.Asset(AssetKinds.Emoji, emojiId, "") : null);
                writer.WriteNumber("count", Int(reaction, "count") ?? 1);
                writer.WriteStartArray("users");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteEmoji(Utf8JsonWriter writer, string? id, string? name, bool animated, string? image)
    {
        writer.WritePropertyName("emoji");
        writer.WriteStartObject();
        writer.WriteString("id", id ?? "");
        writer.WriteString("name", name);
        writer.WriteString("code", name);
        writer.WriteBoolean("isAnimated", animated);
        writer.WriteString("imageUrl", image ?? (id is null ? "" : $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}"));
        writer.WriteEndObject();
    }

    // A user as DCE writes one: what the message's JSON says, filled in from the member as the archive last saw
    // them; the nickname and roles of the moment when the message carries its member, the archive's otherwise.
    private static void WriteUser(Utf8JsonWriter writer, JsonElement? user, ulong id, JsonElement? member, ExportLookups lookups)
    {
        var archived = lookups.Member(id);
        var name = (user is { } u ? String(u, "username") : null) ?? archived?.Username;
        var globalName = (user is { } g ? String(g, "global_name") : null) ?? archived?.GlobalName;
        var nickname = member is { } m ? String(m, "nick") : archived?.Nick;
        var roleIds = member is { } withRoles ? Array(withRoles, "roles").Select(Text).ToList() : RoleIds(archived);
        var roles = roleIds.Select(r => ulong.TryParse(r, out var roleId) ? lookups.Role(roleId) : null).OfType<ArchivedRole>().OrderByDescending(r => r.Position).ToList();
        var avatar = (user is { } a ? String(a, "avatar") : null) ?? archived?.AvatarHash;
        var guildAvatar = (member is { } ga ? String(ga, "avatar") : null) ?? archived?.GuildAvatarHash;

        writer.WriteStartObject();
        writer.WriteString("id", id.ToString());
        writer.WriteString("name", name ?? "Unknown user");
        writer.WriteString("discriminator", "0000");
        writer.WriteString("nickname", nickname ?? globalName ?? name ?? "Unknown user");
        writer.WriteString("color", roles.FirstOrDefault(r => r.Color != 0) is { } colored ? $"#{colored.Color:X6}" : null);
        writer.WriteBoolean("isBot", user is { } b && b.TryGetProperty("bot", out var bot) ? bot.ValueKind == JsonValueKind.True : archived?.Bot == true);
        writer.WriteStartArray("roles");
        foreach (var role in roles)
        {
            writer.WriteStartObject();
            writer.WriteString("id", role.Id.ToString());
            writer.WriteString("name", role.Name);
            writer.WriteString("color", role.Color == 0 ? null : $"#{role.Color:X6}");
            writer.WriteNumber("position", role.Position);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("avatarUrl", (guildAvatar is null ? null : lookups.Asset(AssetKinds.Avatar, id, guildAvatar))
            ?? (avatar is null ? null : lookups.Asset(AssetKinds.Avatar, id, avatar))
            ?? (avatar is null
                ? $"https://cdn.discordapp.com/embed/avatars/{(id >> 22) % 6}.png"
                : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.{(avatar.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png")}"));
        writer.WriteEndObject();
    }

    private static List<string?> RoleIds(ArchivedMember? member)
    {
        if (member is null)
            return [];
        using var ids = JsonDocument.Parse(member.RoleIds);
        return ids.RootElement.ValueKind == JsonValueKind.Array ? ids.RootElement.EnumerateArray().Select(Text).ToList() : [];
    }

    private static void WriteEmbed(Utf8JsonWriter writer, JsonElement embed)
    {
        writer.WriteStartObject();
        writer.WriteString("title", String(embed, "title") ?? "");
        writer.WriteString("url", String(embed, "url"));
        writer.WriteString("timestamp", String(embed, "timestamp"));
        writer.WriteString("description", String(embed, "description") ?? "");
        if (embed.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.Number)
            writer.WriteString("color", $"#{color.GetInt32():X6}");

        if (embed.TryGetProperty("author", out var author))
        {
            writer.WritePropertyName("author");
            writer.WriteStartObject();
            writer.WriteString("name", String(author, "name"));
            writer.WriteString("url", String(author, "url"));
            writer.WriteString("iconUrl", String(author, "icon_url"));
            writer.WriteEndObject();
        }

        WriteImage(writer, embed, "thumbnail", "thumbnail");
        WriteImage(writer, embed, "video", "video");

        writer.WriteStartArray("images");
        if (embed.TryGetProperty("image", out var image))
            WriteImageObject(writer, image);
        writer.WriteEndArray();

        if (embed.TryGetProperty("footer", out var footer))
        {
            writer.WritePropertyName("footer");
            writer.WriteStartObject();
            writer.WriteString("text", String(footer, "text"));
            writer.WriteString("iconUrl", String(footer, "icon_url"));
            writer.WriteEndObject();
        }

        writer.WriteStartArray("fields");
        foreach (var field in Array(embed, "fields"))
        {
            writer.WriteStartObject();
            writer.WriteString("name", String(field, "name"));
            writer.WriteString("value", String(field, "value"));
            writer.WriteBoolean("isInline", field.TryGetProperty("inline", out var inline) && inline.ValueKind == JsonValueKind.True);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("inlineEmojis");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteImage(Utf8JsonWriter writer, JsonElement embed, string from, string to)
    {
        if (!embed.TryGetProperty(from, out var image))
            return;
        writer.WritePropertyName(to);
        WriteImageObject(writer, image);
    }

    private static void WriteImageObject(Utf8JsonWriter writer, JsonElement image)
    {
        writer.WriteStartObject();
        writer.WriteString("url", String(image, "url"));
        WriteNumber(writer, "width", Int(image, "width"));
        WriteNumber(writer, "height", Int(image, "height"));
        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } v)
            writer.WriteNumber(name, v);
    }

    private static void Timestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } v)
            writer.WriteString(name, v);
        else
            writer.WriteNull(name);
    }

    private static IEnumerable<JsonElement> Array(JsonElement? element, string name)
        => element is { } e && e.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static JsonElement? Object(JsonElement? element, string name)
        => element is { } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? Text(value) : null;

    // An id or a name, whether the JSON holds it as a string or a number.
    private static string? Text(JsonElement value) => value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;

    private static int? Int(JsonElement? element, string name)
        => element is { } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? Long(JsonElement? element, string name)
        => element is { } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
