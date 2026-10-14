using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using NetCord.JsonModels;

using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class ArchiveTests
{
    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

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
        var model = JsonSerializer.Deserialize<JsonMessage>(mention, Options)!;
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
        var model = JsonSerializer.Deserialize<JsonMessage>(Message, Options)!;

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

public class DownloadAttemptsTests
{
    [Fact]
    public void Gives_up_on_the_last_allowed_try()
    {
        var attempts = new DownloadAttempts(3);

        Assert.False(attempts.Failed(1));
        Assert.False(attempts.Failed(1));
        Assert.True(attempts.Failed(1));
    }

    [Fact]
    public void Counts_each_attachment_on_its_own()
    {
        var attempts = new DownloadAttempts(2);

        Assert.False(attempts.Failed(1));
        Assert.False(attempts.Failed(2));
        Assert.True(attempts.Failed(1));
    }

    [Fact]
    public void A_success_forgets_earlier_failures()
    {
        var attempts = new DownloadAttempts(2);
        attempts.Failed(1);
        attempts.Succeeded(1);

        Assert.False(attempts.Failed(1));
    }

    [Fact]
    public void Starts_over_after_giving_up()
    {
        var attempts = new DownloadAttempts(2);
        attempts.Failed(1);
        attempts.Failed(1);

        Assert.False(attempts.Failed(1));
    }
}

public class AttachmentStoreTests
{
    [Fact]
    public void S3_storage_needs_all_four_settings()
    {
        var options = Options.Create(new ArchiveOptions { Storage = "s3", S3 = new() { ServiceUrl = "https://fsn1.your-objectstorage.com", Bucket = "thobotto", AccessKey = "key" } });

        var ex = Assert.Throws<InvalidOperationException>(() => IAttachmentStore.Create(options));

        Assert.Contains("SecretKey", ex.Message);
    }

    [Fact]
    public void S3_storage_is_named_after_its_bucket_and_service()
    {
        var options = Options.Create(new ArchiveOptions { Storage = "s3", S3 = new() { ServiceUrl = "https://fsn1.your-objectstorage.com", Bucket = "thobotto", AccessKey = "key", SecretKey = "secret" } });

        var store = IAttachmentStore.Create(options);

        Assert.IsType<S3AttachmentStore>(store);
        Assert.True(store.Enabled);
        Assert.Equal("Bucket thobotto at https://fsn1.your-objectstorage.com", store.Place);
    }

    [Fact]
    public async Task No_storage_keeps_nothing_and_answers()
    {
        var store = IAttachmentStore.Create(Options.Create(new ArchiveOptions()));

        Assert.False(store.Enabled);
        Assert.Null(await store.ProblemAsync(CancellationToken.None));
        Assert.Null(await store.OpenAsync("abc", CancellationToken.None));
    }

    [Fact]
    public async Task A_folder_keeps_one_file_per_content_under_its_hash()
    {
        var folder = Directory.CreateTempSubdirectory("thobotto-archive-").FullName;
        var store = new FolderAttachmentStore(folder);
        try
        {
            var key = await store.SaveAsync([1, 2, 3], CancellationToken.None);
            var again = await store.SaveAsync([1, 2, 3], CancellationToken.None);

            Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", key);
            Assert.Equal(key, again);
            Assert.True(File.Exists(Path.Combine(folder, "03", "90", key)));
            Assert.Null(await store.ProblemAsync(CancellationToken.None));
            await using var file = await store.OpenAsync(key, CancellationToken.None);
            Assert.NotNull(file);
            Assert.Equal(3, file.Length);
            Assert.Null(await store.OpenAsync("0000000000000000000000000000000000000000000000000000000000000000", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class RawModelTests
{
    [Fact]
    public void Writes_a_role_with_its_permissions_as_a_string_and_its_id_too()
    {
        var role = new JsonRole { Id = 1234567890123456789, Name = "Mods", Permissions = (NetCord.Permissions)(1UL << 53 | 8), Position = 3, Hoist = true, Colors = new() { PrimaryColor = new(0xFF0000) } };

        using var written = JsonDocument.Parse(RawJson.OfModel(role));
        var root = written.RootElement;

        Assert.Equal("1234567890123456789", root.GetProperty("id").GetString());
        Assert.Equal("9007199254741000", root.GetProperty("permissions").GetString());
        Assert.Equal("Mods", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("hoist").GetBoolean());
    }

    [Fact]
    public void Writes_a_member_with_its_user_inside_and_role_ids_as_strings()
    {
        var member = new JsonGuildUser { User = new() { Id = 5, Username = "marty", GlobalName = "Marty" }, Nickname = "Boss", RoleIds = [7, 8], JoinedAt = DateTimeOffset.UnixEpoch };

        using var written = JsonDocument.Parse(RawJson.OfModel(member));
        var root = written.RootElement;

        Assert.Equal("marty", root.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("Boss", root.GetProperty("nick").GetString());
        Assert.Equal(["7", "8"], root.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("[\"7\",\"8\"]", RawJson.OfModel(member.RoleIds));
    }

    [Fact]
    public void Merges_a_partial_payload_over_the_stored_json()
    {
        const string stored = """{"id":"1","content":"hello","embeds":[],"pinned":false}""";
        const string partial = """{"id":"1","embeds":[{"title":"T"}],"pinned":true}""";

        using var merged = JsonDocument.Parse(RawJson.Merge(stored, partial));
        var root = merged.RootElement;

        Assert.Equal("hello", root.GetProperty("content").GetString());
        Assert.Equal("T", root.GetProperty("embeds")[0].GetProperty("title").GetString());
        Assert.True(root.GetProperty("pinned").GetBoolean());
        Assert.Equal(partial, RawJson.Merge(null, partial));
    }

    [Fact]
    public void A_fetched_message_keeps_the_member_the_gateway_sent()
    {
        const string stored = """{"id":"1","content":"hello","member":{"nick":"Fishy","roles":["7"]}}""";
        const string fetched = """{"id":"1","content":"hello","pinned":true}""";

        using var kept = JsonDocument.Parse(RawJson.KeepMember(stored, fetched));

        Assert.Equal("Fishy", kept.RootElement.GetProperty("member").GetProperty("nick").GetString());
        Assert.True(kept.RootElement.GetProperty("pinned").GetBoolean());
        Assert.Equal(fetched, RawJson.KeepMember("""{"id":"1"}""", fetched));
        Assert.Equal("""{"member":{"nick":"New"}}""", RawJson.KeepMember(stored, """{"member":{"nick":"New"}}"""));
    }
}
