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

public class DceHeaderTests
{
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
