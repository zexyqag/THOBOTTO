using System.Text.Json;

namespace THOBOTTO.Archive;

// Writes archived messages in DiscordChatExporter's JSON format, which migration tools for other
// platforms read. What DCE has no field for (deletions, earlier versions, purges) goes in extra
// fields that importers ignore. Everything is taken from Discord's raw JSON of the message.
public static class DceWriter
{
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
        [18] = "ThreadCreated",
        [19] = "Reply",
    };

    public static void WriteMessage(
        Utf8JsonWriter writer,
        ArchivedMessage message,
        IReadOnlyList<MessageVersion> versions,
        IReadOnlyList<ArchivedAttachment> attachments,
        Func<ArchivedAttachment, string> attachmentUrl)
    {
        using var raw = message.Raw is null ? null : JsonDocument.Parse(message.Raw);
        var root = raw?.RootElement;

        writer.WriteStartObject();
        writer.WriteString("id", message.Id.ToString());
        writer.WriteString("type", root?.TryGetProperty("type", out var type) == true && Kinds.TryGetValue(type.GetInt32(), out var kind) ? kind : "Default");
        writer.WriteString("timestamp", message.CreatedAt);
        Timestamp(writer, "timestampEdited", message.EditedAt);
        writer.WriteNull("callEndedTimestamp");
        writer.WriteBoolean("isPinned", root?.TryGetProperty("pinned", out var pinned) == true && pinned.GetBoolean());
        writer.WriteString("content", message.Content ?? "");

        writer.WritePropertyName("author");
        WriteUser(writer, root?.TryGetProperty("author", out var author) == true ? author : null, message.AuthorId,
            root?.TryGetProperty("member", out var member) == true ? member : null);

        writer.WriteStartArray("attachments");
        foreach (var attachment in attachments)
        {
            writer.WriteStartObject();
            writer.WriteString("id", attachment.Id.ToString());
            writer.WriteString("url", attachmentUrl(attachment));
            writer.WriteString("fileName", attachment.FileName);
            writer.WriteNumber("fileSizeBytes", attachment.Size);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("embeds");
        foreach (var embed in Array(root, "embeds"))
            WriteEmbed(writer, embed);
        writer.WriteEndArray();

        writer.WriteStartArray("stickers");
        foreach (var sticker in Array(root, "sticker_items"))
        {
            var id = String(sticker, "id");
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("name", String(sticker, "name"));
            writer.WriteString("format", sticker.TryGetProperty("format_type", out var format) && format.GetInt32() == 3 ? "Lottie" : "Png");
            writer.WriteString("sourceUrl", $"https://media.discordapp.net/stickers/{id}.png");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("reactions");
        foreach (var reaction in Array(root, "reactions"))
        {
            var emoji = reaction.GetProperty("emoji");
            var id = String(emoji, "id");
            var animated = emoji.TryGetProperty("animated", out var a) && a.GetBoolean();
            writer.WriteStartObject();
            writer.WritePropertyName("emoji");
            writer.WriteStartObject();
            writer.WriteString("id", id ?? "");
            writer.WriteString("name", String(emoji, "name"));
            writer.WriteString("code", String(emoji, "name"));
            writer.WriteBoolean("isAnimated", animated);
            writer.WriteString("imageUrl", id is null ? "" : $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}");
            writer.WriteEndObject();
            writer.WriteNumber("count", reaction.TryGetProperty("count", out var count) ? count.GetInt32() : 1);
            writer.WriteStartArray("users");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("mentions");
        foreach (var mentioned in Array(root, "mentions"))
            WriteUser(writer, mentioned, ulong.Parse(String(mentioned, "id")!), mentioned.TryGetProperty("member", out var m) ? m : null);
        writer.WriteEndArray();

        if (root?.TryGetProperty("message_reference", out var reference) == true && reference.ValueKind == JsonValueKind.Object)
        {
            writer.WritePropertyName("reference");
            writer.WriteStartObject();
            writer.WriteString("messageId", String(reference, "message_id"));
            writer.WriteString("channelId", String(reference, "channel_id"));
            writer.WriteString("guildId", String(reference, "guild_id"));
            writer.WriteEndObject();
        }

        writer.WriteStartArray("inlineEmojis");
        writer.WriteEndArray();

        // Not in DCE's format; importers skip unknown fields.
        writer.WriteBoolean("isDeleted", message.DeletedAt is not null);
        Timestamp(writer, "deletedTimestamp", message.DeletedAt);
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

    private static void WriteUser(Utf8JsonWriter writer, JsonElement? user, ulong id, JsonElement? member)
    {
        var avatar = user is { } u ? String(u, "avatar") : null;
        var name = user is { } n ? String(n, "username") : null;
        var nickname = (member is { } mm ? String(mm, "nick") : null) ?? (user is { } g ? String(g, "global_name") : null) ?? name;

        writer.WriteStartObject();
        writer.WriteString("id", id.ToString());
        writer.WriteString("name", name ?? "Unknown user");
        writer.WriteString("discriminator", "0000");
        writer.WriteString("nickname", nickname ?? "Unknown user");
        writer.WriteNull("color");
        writer.WriteBoolean("isBot", user is { } b && b.TryGetProperty("bot", out var bot) && bot.GetBoolean());
        writer.WriteStartArray("roles");
        writer.WriteEndArray();
        writer.WriteString("avatarUrl", avatar is null
            ? $"https://cdn.discordapp.com/embed/avatars/{(id >> 22) % 6}.png"
            : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.{(avatar.StartsWith("a_") ? "gif" : "png")}");
        writer.WriteEndObject();
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
            writer.WriteBoolean("isInline", field.TryGetProperty("inline", out var inline) && inline.GetBoolean());
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
        if (image.TryGetProperty("width", out var width))
            writer.WriteNumber("width", width.GetInt32());
        if (image.TryGetProperty("height", out var height))
            writer.WriteNumber("height", height.GetInt32());
        writer.WriteEndObject();
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

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
}
