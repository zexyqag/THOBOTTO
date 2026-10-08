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
