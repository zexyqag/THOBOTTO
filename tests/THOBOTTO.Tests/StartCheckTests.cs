using Microsoft.Extensions.Configuration;

namespace THOBOTTO.Tests;

public class StartCheckTests
{
    private const string Token = "MTIzNDU2Nzg5MDEyMzQ1Njc4.YWJj.xyz";

    private static Dictionary<string, string?> Complete() => new()
    {
        ["Discord:Token"] = Token,
        ["ConnectionStrings:Postgres"] = "Host=db;Username=bot;Password=secret",
        ["Lavalink:BaseAddress"] = "http://lavalink:2333",
        ["GameDig:Path"] = Path.GetTempPath(),
    };

    private static string[] Problems(Dictionary<string, string?> settings)
        => [.. StartCheck.Settings(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())];

    [Fact]
    public void A_complete_configuration_passes()
        => Assert.Empty(Problems(Complete()));

    [Fact]
    public void Lavalink_may_be_left_out()
    {
        var settings = Complete();
        settings.Remove("Lavalink:BaseAddress");
        Assert.Empty(Problems(settings));
    }

    [Fact]
    public void A_missing_token_is_named()
    {
        var settings = Complete();
        settings.Remove("Discord:Token");
        Assert.Equal(["Discord:Token is missing."], Problems(settings));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Bot " + Token)]
    public void A_token_of_the_wrong_shape_is_named(string token)
    {
        var settings = Complete();
        settings["Discord:Token"] = token;
        Assert.Equal(["Discord:Token isn't a bot token."], Problems(settings));
    }

    [Fact]
    public void A_missing_connection_string_is_named()
    {
        var settings = Complete();
        settings["ConnectionStrings:Postgres"] = "";
        Assert.Equal(["ConnectionStrings:Postgres is missing."], Problems(settings));
    }

    [Fact]
    public void A_connection_string_that_doesnt_parse_is_named()
    {
        var settings = Complete();
        settings["ConnectionStrings:Postgres"] = "postgres://bot:secret@db/thobotto";
        var problem = Assert.Single(Problems(settings));
        Assert.StartsWith("ConnectionStrings:Postgres isn't a connection string: ", problem);
    }

    [Theory]
    [InlineData("lavalink:2333")]
    [InlineData("/lavalink")]
    [InlineData("ftp://lavalink:2333")]
    public void A_Lavalink_address_must_be_an_absolute_http_url(string address)
    {
        var settings = Complete();
        settings["Lavalink:BaseAddress"] = address;
        Assert.Equal([$"Lavalink:BaseAddress ({address}) isn't an absolute http(s) URL."], Problems(settings));
    }

    [Fact]
    public void A_missing_GameDig_path_is_named()
    {
        var settings = Complete();
        settings.Remove("GameDig:Path");
        Assert.Equal(["GameDig:Path is missing."], Problems(settings));
    }

    [Fact]
    public void A_GameDig_folder_that_isnt_there_is_named()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var settings = Complete();
        settings["GameDig:Path"] = folder;
        Assert.Equal([$"GameDig:Path ({folder}) isn't a folder."], Problems(settings));
    }

    [Fact]
    public void A_keys_folder_not_there_yet_passes_and_isnt_kept()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var settings = Complete();
        settings["DataProtection:Directory"] = folder;
        Assert.Empty(Problems(settings));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_keys_folder_that_cant_be_made_is_named()
    {
        var file = Path.GetTempFileName();
        try
        {
            var folder = Path.Combine(file, "keys");
            var settings = Complete();
            settings["DataProtection:Directory"] = folder;
            Assert.Equal([$"DataProtection:Directory ({folder}) isn't a writable folder."], Problems(settings));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Every_problem_is_named_at_once()
        => Assert.Equal(["Discord:Token is missing.", "ConnectionStrings:Postgres is missing.", "GameDig:Path is missing."], Problems([]));
}
