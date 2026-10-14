using System.Text.Json;

using NetCord;

using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class ThobottoWriterTests
{
    private static readonly DateTimeOffset Posted = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Raw = """
        {"id":"10","type":0,"content":"second take","author":{"id":"7","username":"marty"},
         "poll":{"question":{"text":"Pizza?"},"answers":[{"answer_id":1,"poll_media":{"text":"Yes"}},{"answer_id":2,"poll_media":{"text":"No"}}]}}
        """;

    private static JsonElement Written(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            write(writer);
        return JsonDocument.Parse(stream.ToArray()).RootElement;
    }

    [Fact]
    public void Writes_a_message_whole_with_its_versions_reactions_votes_and_attachments()
    {
        var message = new ArchivedMessage { Id = 10, GuildId = 1, ChannelId = 5, AuthorId = 7, CreatedAt = Posted, EditedAt = Posted.AddMinutes(1), Content = "second take", Raw = Raw, DeletedAt = Posted.AddHours(1), DeletedById = 8 };
        MessageVersion[] versions = [new() { MessageId = 10, Content = "first take", Raw = """{"id":"10","content":"first take"}""", ReplacedAt = Posted.AddMinutes(1) }];
        ArchivedAttachment[] attachments =
        [
            new() { Id = 11, MessageId = 10, FileName = "cat.png", ContentType = "image/png", Size = 42, Url = "https://cdn/cat.png", StoredKey = "abcdef0123456789ff", StoredAt = Posted },
            new() { Id = 12, MessageId = 10, FileName = "dog.png", Size = 7, Url = "https://cdn/dog.png", FailedAt = Posted },
        ];
        ArchivedReaction[] reactions =
        [
            new() { MessageId = 10, UserId = 8, EmojiName = "😂", AddedAt = Posted.AddMinutes(2) },
            new() { MessageId = 10, UserId = 9, EmojiId = 500, EmojiName = "kek", Burst = true, AddedAt = Posted.AddMinutes(3), RemovedAt = Posted.AddMinutes(4) },
        ];
        ArchivedPollVote[] votes = [new() { MessageId = 10, UserId = 8, AnswerId = 2, AddedAt = Posted.AddMinutes(5) }];

        var m = Written(w => ThobottoWriter.WriteMessage(w, message, versions, attachments, reactions, votes, a => a.StoredKey is null ? null : "attachments/cat-abcdef0123456789.png"));

        Assert.Equal("10", m.GetProperty("id").GetString());
        Assert.Equal("7", m.GetProperty("authorId").GetString());
        Assert.Equal("8", m.GetProperty("deletedById").GetString());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("purgedAt").ValueKind);
        Assert.Equal("Pizza?", m.GetProperty("raw").GetProperty("poll").GetProperty("question").GetProperty("text").GetString());
        var version = Assert.Single(m.GetProperty("versions").EnumerateArray());
        Assert.Equal("first take", version.GetProperty("content").GetString());
        Assert.Equal("first take", version.GetProperty("raw").GetProperty("content").GetString());
        var files = m.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal("attachments/cat-abcdef0123456789.png", files[0].GetProperty("file").GetString());
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("file").ValueKind);
        Assert.Equal("https://cdn/dog.png", files[1].GetProperty("url").GetString());
        var reacted = m.GetProperty("reactions").EnumerateArray().ToList();
        Assert.Equal("8", reacted[0].GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, reacted[0].GetProperty("emojiId").ValueKind);
        Assert.Equal("500", reacted[1].GetProperty("emojiId").GetString());
        Assert.True(reacted[1].GetProperty("burst").GetBoolean());
        Assert.Equal(Posted.AddMinutes(4), reacted[1].GetProperty("removedAt").GetDateTimeOffset());
        var vote = Assert.Single(m.GetProperty("pollVotes").EnumerateArray());
        Assert.Equal(2, vote.GetProperty("answerId").GetInt32());
        Assert.Equal("8", vote.GetProperty("userId").GetString());
    }

    [Fact]
    public void A_purged_message_keeps_only_who_where_and_when()
    {
        var message = new ArchivedMessage { Id = 10, GuildId = 1, ChannelId = 5, AuthorId = 7, CreatedAt = Posted, PurgedAt = Posted.AddDays(1) };

        var m = Written(w => ThobottoWriter.WriteMessage(w, message, [], [], [], [], _ => null));

        Assert.Equal(JsonValueKind.Null, m.GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("raw").ValueKind);
        Assert.Equal(Posted.AddDays(1), m.GetProperty("purgedAt").GetDateTimeOffset());
        Assert.Equal("5", m.GetProperty("channelId").GetString());
    }

    [Fact]
    public void Writes_a_thread_with_its_channel_and_a_channel_the_snapshot_never_saw()
    {
        var thread = new ArchivedChannel { Id = 20, GuildId = 1, ParentId = 5, Type = ChannelType.PublicGuildThread, Name = "plans", AppliedTags = """["1","2"]""", OwnerId = 7, ThreadCreatedAt = Posted, Archived = true, Raw = """{"id":"20","name":"plans"}""", FirstSeenAt = Posted, LastSeenAt = Posted };

        var written = Written(w =>
        {
            w.WriteStartArray();
            ThobottoWriter.WriteChannel(w, thread, "messages/20.jsonl");
            ThobottoWriter.WriteUnknownChannel(w, 30, "old-general", "messages/30.jsonl");
            w.WriteEndArray();
        });

        var t = written[0];
        Assert.True(t.GetProperty("inSnapshot").GetBoolean());
        Assert.True(t.GetProperty("thread").GetBoolean());
        Assert.Equal(11, t.GetProperty("type").GetInt32());
        Assert.Equal("5", t.GetProperty("parentId").GetString());
        Assert.Equal(["1", "2"], t.GetProperty("appliedTags").EnumerateArray().Select(e => e.GetString()));
        Assert.True(t.GetProperty("archived").GetBoolean());
        Assert.Equal("plans", t.GetProperty("raw").GetProperty("name").GetString());
        var unknown = written[1];
        Assert.False(unknown.GetProperty("inSnapshot").GetBoolean());
        Assert.Equal("old-general", unknown.GetProperty("name").GetString());
        Assert.Equal("messages/30.jsonl", unknown.GetProperty("messages").GetString());
    }

    [Fact]
    public void Writes_each_emoji_of_the_snapshot_with_its_file()
    {
        const string emojis = """[{"id":"500","name":"kek","animated":false},{"id":"501","name":"lol","animated":true}]""";

        var written = Written(w => ThobottoWriter.WriteWithFiles(w, emojis, id => id == 500 ? "assets/emoji/500.png" : null));

        Assert.Equal("kek", written[0].GetProperty("name").GetString());
        Assert.Equal("assets/emoji/500.png", written[0].GetProperty("file").GetString());
        Assert.Equal(JsonValueKind.Null, written[1].GetProperty("file").ValueKind);
        Assert.Empty(Written(w => ThobottoWriter.WriteWithFiles(w, null, _ => null)).EnumerateArray());
    }

    [Fact]
    public void The_manifest_says_what_the_export_is()
    {
        var counts = new ExportCounts { Messages = 3, Files = 9, AttachmentsWithoutFile = 1 };
        GuildSnapshot[] snapshots = [new() { GuildId = 1, Kind = SnapshotKinds.Guild, TakenAt = Posted, TriedAt = Posted }, new() { GuildId = 1, Kind = SnapshotKinds.Bans, TriedAt = Posted, Problem = "no Ban Members permission" }];

        var m = Written(w => ThobottoWriter.WriteManifest(w, new(Posted.AddDays(1), 1, "Friends", "assets/icon/1-abc.png", """{"id":"1","name":"Friends"}""", snapshots, counts, ["A note."], ["quotes"])));

        Assert.Equal("thobotto-export/1", m.GetProperty("format").GetString());
        Assert.Equal("Friends", m.GetProperty("server").GetProperty("name").GetString());
        Assert.Equal(Posted, m.GetProperty("server").GetProperty("snapshotTakenAt").GetDateTimeOffset());
        Assert.Equal("Friends", m.GetProperty("guild").GetProperty("name").GetString());
        Assert.Equal(3, m.GetProperty("counts").GetProperty("messages").GetInt32());
        Assert.Equal(1, m.GetProperty("counts").GetProperty("attachmentsWithoutFile").GetInt32());
        Assert.Equal("no Ban Members permission", m.GetProperty("snapshots").GetProperty("bans").GetProperty("problem").GetString());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("snapshots").GetProperty("bans").GetProperty("takenAt").ValueKind);
        Assert.Equal("A note.", m.GetProperty("notes")[0].GetString());
        Assert.Equal("bot/quotes.json", m.GetProperty("files").GetProperty("bot")[0].GetString());
    }
}

public class DceWriterTests
{
    private static readonly DateTimeOffset Posted = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Raw = """
        {"id":"10","type":19,"content":"second take","pinned":true,
         "author":{"id":"7","username":"marty","global_name":"Marty","avatar":"abc"},
         "member":{"nick":"Boss","roles":["77"]},
         "attachments":[{"id":"11","filename":"cat.png","size":42,"content_type":"image/png","width":10,"height":20,"description":"A cat"}],
         "embeds":[{"title":"T","description":"D","color":16711680,"fields":[{"name":"f","value":"v","inline":true}]}],
         "sticker_items":[{"id":"600","name":"dance","format_type":4}],
         "reactions":[{"emoji":{"id":null,"name":"😂"},"count":3}],
         "components":[{"type":1,"components":[{"type":2,"custom_id":"bet:1:0","label":"Yes","style":1}]}],
         "message_reference":{"message_id":"9","channel_id":"5","guild_id":"1"},
         "interaction":{"id":"800","type":2,"name":"bet","user":{"id":"7","username":"marty"}}}
        """;

    private static readonly ArchivedRole Mods = new() { Id = 77, GuildId = 1, Name = "Mods", Color = 0xFF0000, Position = 3, Raw = "{}" };
    private static readonly ArchivedMember Goldfish = new() { GuildId = 1, UserId = 8, Username = "goldfish", GlobalName = "Gold", Nick = "Fishy", RoleIds = """["77"]""", AvatarHash = "fff", Raw = "{}" };

    private static readonly ExportLookups Lookups = new(
        id => id == 8 ? Goldfish : null,
        id => id == 77 ? Mods : null,
        (kind, id, hash) => (kind, id, hash) switch
        {
            (AssetKinds.Avatar, 8, "fff") => "assets/avatar/8-fff.png",
            (AssetKinds.Emoji, 500, "") => "assets/emoji/500.gif",
            (AssetKinds.Sticker, 600, "") => "assets/sticker/600.gif",
            _ => null,
        });

    private static ArchivedMessage Message(string? raw) => new()
    {
        Id = 10,
        GuildId = 1,
        ChannelId = 5,
        AuthorId = 7,
        CreatedAt = Posted,
        Content = "second take",
        Raw = raw,
    };

    private static JsonElement Written(ArchivedMessage message, IReadOnlyList<MessageVersion>? versions = null, IReadOnlyList<ArchivedAttachment>? attachments = null,
        IReadOnlyList<ArchivedReaction>? reactions = null, IReadOnlyList<ArchivedPollVote>? votes = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            DceWriter.WriteMessage(writer, message, versions ?? [], attachments ?? [], reactions ?? [], votes ?? [], a => a.StoredKey is null ? a.Url : $"attachments/{a.FileName}", Lookups);
        return JsonDocument.Parse(stream.ToArray()).RootElement;
    }

    [Fact]
    public void Writes_discord_chat_exporter_messages_with_our_extras()
    {
        var message = Message(Raw);
        message.EditedAt = Posted.AddMinutes(1);
        MessageVersion[] versions = [new() { MessageId = 10, Content = "first take", ReplacedAt = Posted.AddMinutes(1) }];
        ArchivedAttachment[] attachments = [new() { Id = 11, MessageId = 10, FileName = "cat.png", Size = 42, Url = "https://cdn/cat.png", StoredKey = "abcdef0123456789ff" }];

        var m = Written(message, versions, attachments);

        Assert.Equal("Reply", m.GetProperty("type").GetString());
        Assert.Equal(19, m.GetProperty("rawType").GetInt32());
        Assert.True(m.GetProperty("isPinned").GetBoolean());
        var author = m.GetProperty("author");
        Assert.Equal("Boss", author.GetProperty("nickname").GetString());
        Assert.Equal("marty", author.GetProperty("name").GetString());
        Assert.Equal("#FF0000", author.GetProperty("color").GetString());
        Assert.Equal("Mods", author.GetProperty("roles")[0].GetProperty("name").GetString());
        Assert.Equal("https://cdn.discordapp.com/avatars/7/abc.png", author.GetProperty("avatarUrl").GetString());
        Assert.Equal("#FF0000", m.GetProperty("embeds")[0].GetProperty("color").GetString());
        Assert.True(m.GetProperty("embeds")[0].GetProperty("fields")[0].GetProperty("isInline").GetBoolean());
        var attachment = m.GetProperty("attachments")[0];
        Assert.Equal("attachments/cat.png", attachment.GetProperty("url").GetString());
        Assert.Equal("image/png", attachment.GetProperty("contentType").GetString());
        Assert.Equal(20, attachment.GetProperty("height").GetInt32());
        Assert.Equal("A cat", attachment.GetProperty("description").GetString());
        Assert.False(attachment.GetProperty("isSpoiler").GetBoolean());
        var sticker = m.GetProperty("stickers")[0];
        Assert.Equal("Gif", sticker.GetProperty("format").GetString());
        Assert.Equal("assets/sticker/600.gif", sticker.GetProperty("sourceUrl").GetString());
        Assert.Equal(3, m.GetProperty("reactions")[0].GetProperty("count").GetInt32());
        Assert.Empty(m.GetProperty("reactions")[0].GetProperty("users").EnumerateArray());
        Assert.Equal("9", m.GetProperty("reference").GetProperty("messageId").GetString());
        Assert.Equal("bet", m.GetProperty("interaction").GetProperty("name").GetString());
        Assert.Equal("7", m.GetProperty("interaction").GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("bet:1:0", m.GetProperty("components")[0].GetProperty("components")[0].GetProperty("custom_id").GetString());
        Assert.Equal("first take", m.GetProperty("edits")[0].GetProperty("content").GetString());
        Assert.False(m.GetProperty("isDeleted").GetBoolean());
        Assert.Empty(m.GetProperty("forwarded").EnumerateArray());
        Assert.False(m.TryGetProperty("poll", out _));
    }

    [Fact]
    public void Names_every_message_type_and_keeps_the_number_of_an_unknown_one()
    {
        Assert.Equal("ChatInputCommand", Written(Message("""{"id":"10","type":20}""")).GetProperty("type").GetString());
        Assert.Equal("PollResult", Written(Message("""{"id":"10","type":46}""")).GetProperty("type").GetString());
        var unknown = Written(Message("""{"id":"10","type":99}"""));
        Assert.Equal("99", unknown.GetProperty("type").GetString());
        Assert.Equal(99, unknown.GetProperty("rawType").GetInt32());
        Assert.Equal("Default", Written(Message(null)).GetProperty("type").GetString());
    }

    [Fact]
    public void A_forward_carries_the_forwarded_message()
    {
        const string raw = """
            {"id":"10","type":0,"content":"","message_reference":{"type":1,"message_id":"9","channel_id":"5"},
             "message_snapshots":[{"message":{"type":0,"content":"the original","timestamp":"2026-09-01T10:00:00+00:00","edited_timestamp":null,
               "attachments":[{"id":"12","filename":"doc.pdf","size":100,"url":"https://cdn/doc.pdf"}],"embeds":[{"title":"E"}],"sticker_items":[]}}]}
            """;

        var forwarded = Assert.Single(Written(Message(raw)).GetProperty("forwarded").EnumerateArray());

        Assert.Equal("the original", forwarded.GetProperty("content").GetString());
        Assert.Equal("2026-09-01T10:00:00+00:00", forwarded.GetProperty("timestamp").GetString());
        Assert.Equal("doc.pdf", forwarded.GetProperty("attachments")[0].GetProperty("fileName").GetString());
        Assert.Equal(100, forwarded.GetProperty("attachments")[0].GetProperty("fileSizeBytes").GetInt64());
        Assert.Equal("E", forwarded.GetProperty("embeds")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void A_poll_has_its_answers_with_who_voted_from_the_rows()
    {
        const string raw = """
            {"id":"10","type":0,"content":"","poll":{"question":{"text":"Pizza?"},"allow_multiselect":false,"layout_type":1,"expiry":"2026-10-02T12:00:00+00:00",
             "answers":[{"answer_id":1,"poll_media":{"text":"Yes","emoji":{"id":null,"name":"🍕"}}},{"answer_id":2,"poll_media":{"text":"No"}}],
             "results":{"is_finalized":false,"answer_counts":[{"id":1,"count":9,"me_voted":false}]}}}
            """;
        ArchivedPollVote[] votes =
        [
            new() { MessageId = 10, UserId = 8, AnswerId = 1, AddedAt = Posted },
            new() { MessageId = 10, UserId = 9, AnswerId = 1, AddedAt = Posted, RemovedAt = Posted.AddMinutes(1) },
            new() { MessageId = 10, UserId = 9, AnswerId = 2, AddedAt = Posted.AddMinutes(1) },
        ];

        var poll = Written(Message(raw), votes: votes).GetProperty("poll");

        Assert.Equal("Pizza?", poll.GetProperty("question").GetString());
        Assert.Equal("2026-10-02T12:00:00+00:00", poll.GetProperty("expiresAt").GetString());
        var answers = poll.GetProperty("answers").EnumerateArray().ToList();
        Assert.Equal("Yes", answers[0].GetProperty("text").GetString());
        Assert.Equal("🍕", answers[0].GetProperty("emoji").GetProperty("name").GetString());
        Assert.Equal(1, answers[0].GetProperty("count").GetInt32());
        Assert.Equal(["8"], answers[0].GetProperty("voters").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["9"], answers[1].GetProperty("voters").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(JsonValueKind.Null, answers[1].GetProperty("emoji").ValueKind);

        // Without rows, the counts Discord last gave.
        var counted = Written(Message(raw)).GetProperty("poll").GetProperty("answers").EnumerateArray().ToList();
        Assert.Equal(9, counted[0].GetProperty("count").GetInt32());
        Assert.Equal(0, counted[1].GetProperty("count").GetInt32());
    }

    [Fact]
    public void Reactions_come_from_the_rows_with_who_reacted_as_the_archive_knows_them()
    {
        ArchivedReaction[] reactions =
        [
            new() { MessageId = 10, UserId = 8, EmojiName = "😂", AddedAt = Posted },
            new() { MessageId = 10, UserId = 7, EmojiName = "😂", AddedAt = Posted.AddMinutes(1) },
            new() { MessageId = 10, UserId = 8, EmojiId = 500, EmojiName = "kek", AddedAt = Posted.AddMinutes(2) },
            new() { MessageId = 10, UserId = 9, EmojiName = "😂", AddedAt = Posted.AddMinutes(3), RemovedAt = Posted.AddMinutes(4) },
        ];

        var written = Written(Message(Raw), reactions: reactions).GetProperty("reactions").EnumerateArray().ToList();

        Assert.Equal(2, written.Count);
        Assert.Equal("😂", written[0].GetProperty("emoji").GetProperty("name").GetString());
        Assert.Equal(2, written[0].GetProperty("count").GetInt32());
        var goldfish = written[0].GetProperty("users")[0];
        Assert.Equal("goldfish", goldfish.GetProperty("name").GetString());
        Assert.Equal("Fishy", goldfish.GetProperty("nickname").GetString());
        Assert.Equal("#FF0000", goldfish.GetProperty("color").GetString());
        Assert.Equal("77", goldfish.GetProperty("roles")[0].GetProperty("id").GetString());
        Assert.Equal("assets/avatar/8-fff.png", goldfish.GetProperty("avatarUrl").GetString());
        Assert.Equal("Unknown user", written[0].GetProperty("users")[1].GetProperty("name").GetString());
        var kek = written[1].GetProperty("emoji");
        Assert.Equal("500", kek.GetProperty("id").GetString());
        Assert.True(kek.GetProperty("isAnimated").GetBoolean());
        Assert.Equal("assets/emoji/500.gif", kek.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public void A_message_without_its_member_takes_the_nickname_and_roles_the_archive_last_saw()
    {
        var message = Message("""{"id":"10","type":0,"content":"hi","author":{"id":"8","username":"goldfish"}}""");
        message = new() { Id = 10, GuildId = 1, ChannelId = 5, AuthorId = 8, CreatedAt = Posted, Content = "hi", Raw = message.Raw };

        var author = Written(message).GetProperty("author");

        Assert.Equal("Fishy", author.GetProperty("nickname").GetString());
        Assert.Equal("Mods", author.GetProperty("roles")[0].GetProperty("name").GetString());
        Assert.Equal("assets/avatar/8-fff.png", author.GetProperty("avatarUrl").GetString());
    }

    [Fact]
    public void A_thread_sits_under_its_channel_as_its_category()
    {
        var channel = new ArchivedChannel { Id = 5, GuildId = 1, Type = ChannelType.TextGuildChannel, Name = "general", Raw = "{}" };
        var thread = new ArchivedChannel { Id = 20, GuildId = 1, ParentId = 5, Type = ChannelType.PrivateGuildThread, Name = "plans", Raw = "{}" };

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            DceWriter.WriteStart(writer, 1, "Friends", "", thread, channel, DateTimeOffset.UnixEpoch);
            DceWriter.WriteEnd(writer, 0);
        }
        var root = JsonDocument.Parse(stream.ToArray()).RootElement;

        Assert.Equal("Friends", root.GetProperty("guild").GetProperty("name").GetString());
        Assert.Equal("GuildPrivateThread", root.GetProperty("channel").GetProperty("type").GetString());
        Assert.Equal("5", root.GetProperty("channel").GetProperty("categoryId").GetString());
        Assert.Equal("general", root.GetProperty("channel").GetProperty("category").GetString());
        Assert.Equal(0, root.GetProperty("messageCount").GetInt32());
        Assert.Equal("GuildForum", DceWriter.Kind(ChannelType.ForumGuildChannel));
        Assert.Equal("GuildTextChat", DceWriter.Kind(ChannelType.TextGuildChannel));
    }
}

public class ExportSourcesTests
{
    private sealed class Rows(string name) : IExportSource
    {
        public string Name => name;

        public Task WriteAsync(ulong guildId, Utf8JsonWriter json)
        {
            json.WriteStartArray();
            json.WriteStartObject();
            json.WriteId("server", guildId);
            json.WriteEndObject();
            json.WriteEndArray();
            return Task.CompletedTask;
        }
    }

    private sealed class WithFiles : IExportSource, IExportFiles
    {
        public string Name => "sounds";

        public Task WriteAsync(ulong guildId, Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteString("file", ExportFolder.PathOf(Name, "1-horn.mp3"));
            json.WriteEndObject();
            return Task.CompletedTask;
        }

        public Task WriteFilesAsync(ulong guildId, ExportFolder folder) => folder.SaveAsync("1-horn.mp3", [1, 2, 3]);
    }

    [Fact]
    public async Task Writes_each_source_under_bot_and_its_files_under_assets()
    {
        var folder = Directory.CreateTempSubdirectory("thobotto-export-").FullName;
        try
        {
            var (names, files) = await ExportSources.WriteAsync(folder, 42, [new Rows("quotes"), new WithFiles()], CancellationToken.None);

            Assert.Equal(["quotes", "sounds"], names);
            Assert.Equal(3, files);
            var quotes = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "bot", "quotes.json"))).RootElement;
            Assert.Equal("42", quotes[0].GetProperty("server").GetString());
            var sounds = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "bot", "sounds.json"))).RootElement;
            Assert.Equal("assets/sounds/1-horn.mp3", sounds.GetProperty("file").GetString());
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(folder, "assets", "sounds", "1-horn.mp3")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_last_export_is_read_back_from_its_row_alone()
    {
        var record = new ExportRecord(new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), "/export", 42, 1091);
        var row = new GuildSnapshot { GuildId = 1, Kind = SnapshotKinds.Export, TakenAt = record.At, TriedAt = record.At, Json = record.ToJson() };

        Assert.Equal(record, ExportRecord.Parse(row));
        Assert.Null(ExportRecord.Parse(new GuildSnapshot { GuildId = 1, Kind = SnapshotKinds.Guild, Json = "{}" }));
        Assert.Null(ExportRecord.Parse(null));
    }

    [Fact]
    public void A_file_name_keeps_what_every_file_system_takes()
    {
        Assert.Equal("a_b_c", ExportFolder.Safe("a/b\\c"));
        Assert.Equal(42, ExportFolder.Safe(new string('x', 60)).Length);
        Assert.Equal("", ExportFolder.Safe(null));
    }
}
