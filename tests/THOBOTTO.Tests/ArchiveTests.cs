using System.Text.Json;
using System.Text.Json.Serialization;

using NetCord.JsonModels;

using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class ArchiveTests
{
    // A bot message with an embed and a button row, as Discord sends it.
    private const string Message = """
        {"id":"1557610020953260084","channel_id":"1557576141722746993","type":0,"content":"Will it work?",
         "author":{"id":"772798337883439115","username":"THOBOT.test","discriminator":"0","bot":true},
         "timestamp":"2026-10-08T04:20:00.000000+00:00","tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],
         "attachments":[],"pinned":false,
         "embeds":[{"type":"rich","title":"Bet 1","description":"Pick one"}],
         "components":[{"type":1,"id":1,"components":[{"type":2,"id":2,"custom_id":"bet:1:0","label":"Yes","style":1}]}]}
        """;

    [Fact]
    public void Writes_mentions_whose_member_points_back_at_the_user()
    {
        // Discord sends a mention's member without its user; NetCord links them both ways.
        const string mention = """
            {"id":"1","channel_id":"2","type":0,"content":"<@3> hi","author":{"id":"4","username":"a"},
             "timestamp":"2026-10-08T12:00:00+00:00","tts":false,"mention_everyone":false,"mention_roles":[],"attachments":[],"embeds":[],"pinned":false,
             "mentions":[{"id":"3","username":"goldfish","member":{"nick":"Fishy","roles":[],"joined_at":"2026-10-07T00:00:00+00:00","deaf":false,"mute":false}}]}
            """;
        var model = JsonSerializer.Deserialize<JsonMessage>(mention, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString })!;
        foreach (var user in model.MentionedUsers!)
            if (user.GuildUser is { } member)
                member.User = user;

        using var written = JsonDocument.Parse(RawJson.Of(model));
        var mentioned = written.RootElement.GetProperty("mentions")[0];

        Assert.Equal("goldfish", mentioned.GetProperty("username").GetString());
        Assert.Equal("Fishy", mentioned.GetProperty("member").GetProperty("nick").GetString());
    }

    [Fact]
    public void Writes_messages_with_components_back_to_json()
    {
        var model = JsonSerializer.Deserialize<JsonMessage>(Message, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString })!;

        using var written = JsonDocument.Parse(RawJson.Of(model));
        var root = written.RootElement;

        Assert.Equal("Will it work?", root.GetProperty("content").GetString());
        Assert.Equal("Bet 1", root.GetProperty("embeds")[0].GetProperty("title").GetString());
        var button = root.GetProperty("components")[0].GetProperty("components")[0];
        Assert.Equal("bet:1:0", button.GetProperty("custom_id").GetString());
        Assert.Equal("Yes", button.GetProperty("label").GetString());
        Assert.Equal("1557610020953260084", root.GetProperty("id").GetString());
    }
}

public class DceWriterTests
{
    private const string Raw = """
        {"id":"10","type":19,"content":"second take","pinned":true,
         "author":{"id":"7","username":"marty","global_name":"Marty","avatar":"abc"},
         "member":{"nick":"Boss"},
         "embeds":[{"title":"T","description":"D","color":16711680,"fields":[{"name":"f","value":"v","inline":true}]}],
         "reactions":[{"emoji":{"id":null,"name":"😂"},"count":3}],
         "message_reference":{"message_id":"9","channel_id":"5","guild_id":"1"}}
        """;

    [Fact]
    public void Writes_discord_chat_exporter_messages_with_our_extras()
    {
        var message = new ArchivedMessage
        {
            Id = 10, GuildId = 1, ChannelId = 5, AuthorId = 7,
            CreatedAt = DateTimeOffset.UnixEpoch, EditedAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
            Content = "second take", Raw = Raw,
        };
        MessageVersion[] versions = [new() { MessageId = 10, Content = "first take", ReplacedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) }];
        ArchivedAttachment[] attachments = [new() { Id = 11, MessageId = 10, FileName = "cat.png", Size = 42, Url = "https://cdn/cat.png", StoredKey = "abcdef0123456789ff" }];

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            DceWriter.WriteMessage(writer, message, versions, attachments, a => $"attachments/{a.FileName}");
        var m = JsonDocument.Parse(stream.ToArray()).RootElement;

        Assert.Equal("Reply", m.GetProperty("type").GetString());
        Assert.True(m.GetProperty("isPinned").GetBoolean());
        Assert.Equal("Boss", m.GetProperty("author").GetProperty("nickname").GetString());
        Assert.Equal("marty", m.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("#FF0000", m.GetProperty("embeds")[0].GetProperty("color").GetString());
        Assert.True(m.GetProperty("embeds")[0].GetProperty("fields")[0].GetProperty("isInline").GetBoolean());
        Assert.Equal("attachments/cat.png", m.GetProperty("attachments")[0].GetProperty("url").GetString());
        Assert.Equal(3, m.GetProperty("reactions")[0].GetProperty("count").GetInt32());
        Assert.Equal("9", m.GetProperty("reference").GetProperty("messageId").GetString());
        Assert.Equal("first take", m.GetProperty("edits")[0].GetProperty("content").GetString());
        Assert.False(m.GetProperty("isDeleted").GetBoolean());
    }
}
