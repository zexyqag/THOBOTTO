using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class LavalinkPluginsTests : IDisposable
{
    private const string YoutubeUrl = "https://maven.example/snapshots/youtube-plugin/abc/youtube-plugin-abc.jar";
    private const string LavasrcUrl = "https://maven.example/releases/lavasrc-plugin/4.8.3/lavasrc-plugin-4.8.3.jar";
    private static readonly byte[] Youtube = Encoding.ASCII.GetBytes("the youtube plugin");
    private static readonly byte[] Lavasrc = Encoding.ASCII.GetBytes("the lavasrc plugin");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "thobotto-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, (byte[] Body, string Type)> _served = new()
    {
        [YoutubeUrl] = (Youtube, "application/java-archive"),
        [LavasrcUrl] = (Lavasrc, "application/java-archive"),
    };
    private int _requests;

    private static string Sum(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Path_(string file) => Path.Combine(_folder, file);

    private LavalinkPlugins Make(params (string Name, string? Url, string? Sha256)[] plugins)
    {
        var options = new LavalinkOptions { BaseAddress = "http://lavalink", Passphrase = "x", PluginsDirectory = _folder };
        foreach (var (name, url, sha256) in plugins)
            options.Plugins[name] = new() { Url = url, Sha256 = sha256 };
        return new(Options.Create(options), new HttpClient(new Handler(this)), NullLogger<LavalinkPlugins>.Instance);
    }

    private LavalinkPlugins MakeBoth() => Make(("youtube-plugin", YoutubeUrl, Sum(Youtube)), ("lavasrc-plugin", LavasrcUrl, Sum(Lavasrc)));

    [Fact]
    public async Task Fetches_what_is_missing()
    {
        var plugins = MakeBoth();
        await plugins.ProvideAsync(CancellationToken.None);
        Assert.True(plugins.Tried);
        Assert.Equal(Youtube, await File.ReadAllBytesAsync(Path_("youtube-plugin-abc.jar")));
        Assert.Equal(Lavasrc, await File.ReadAllBytesAsync(Path_("lavasrc-plugin-4.8.3.jar")));
        Assert.All(plugins.Provided, p => Assert.Null(p.Problem));
        Assert.Equal(["abc", "4.8.3"], plugins.Provided.Select(p => p.Version));
        Assert.Equal(2, _requests);
    }

    [Fact]
    public async Task Leaves_matching_files_alone_without_asking()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(Path_("youtube-plugin-abc.jar"), Youtube);
        await File.WriteAllBytesAsync(Path_("lavasrc-plugin-4.8.3.jar"), Lavasrc);
        var plugins = MakeBoth();
        await plugins.ProvideAsync(CancellationToken.None);
        Assert.Equal(0, _requests);
        Assert.All(plugins.Provided, p => Assert.Null(p.Problem));
    }

    [Fact]
    public async Task Replaces_a_wrong_file_and_removes_the_rest()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path_("youtube-plugin-abc.jar"), "<html>a listing</html>");
        await File.WriteAllBytesAsync(Path_("lavasrc-plugin-4.8.3.jar"), Lavasrc);
        await File.WriteAllTextAsync(Path_("youtube-plugin-1.18.2.jar"), "an old one");
        await File.WriteAllTextAsync(Path_("lavasrc-plugin-4.8.3.jar.partial"), "half");
        var plugins = MakeBoth();
        await plugins.ProvideAsync(CancellationToken.None);
        Assert.Equal(1, _requests);
        Assert.Equal(Youtube, await File.ReadAllBytesAsync(Path_("youtube-plugin-abc.jar")));
        Assert.Equal(["lavasrc-plugin-4.8.3.jar", "youtube-plugin-abc.jar"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_wrong_sum_leaves_nothing_behind_and_says_so()
    {
        var plugins = Make(("youtube-plugin", YoutubeUrl, new string('0', 64)));
        await plugins.ProvideAsync(CancellationToken.None);
        var youtube = Assert.Single(plugins.Provided);
        Assert.Contains("isn't the file expected", youtube.Problem);
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.True(plugins.Tried);
    }

    [Fact]
    public async Task A_web_page_instead_of_the_file_is_reported()
    {
        _served[YoutubeUrl] = (Encoding.ASCII.GetBytes("<html>Index of /snapshots</html>"), "text/html");
        var plugins = Make(("youtube-plugin", YoutubeUrl, Sum(Youtube)));
        await plugins.ProvideAsync(CancellationToken.None);
        Assert.Contains("web page", Assert.Single(plugins.Provided).Problem);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task An_unreachable_address_is_reported()
    {
        var plugins = Make(("youtube-plugin", "https://maven.example/missing/youtube-plugin-abc.jar", Sum(Youtube)));
        await plugins.ProvideAsync(CancellationToken.None);
        Assert.Contains("404", Assert.Single(plugins.Provided).Problem);
    }

    [Fact]
    public async Task Bad_settings_are_reported_without_fetching()
    {
        var plugins = Make(("youtube-plugin", YoutubeUrl, "notasum"), ("lavasrc-plugin", "https://maven.example/lavasrc-plugin", Sum(Lavasrc)), ("gone", "", null));
        await plugins.ProvideAsync(CancellationToken.None);
        Assert.Equal(0, _requests);
        Assert.Equal(["youtube-plugin", "lavasrc-plugin"], plugins.Provided.Select(p => p.Name));
        Assert.Contains("sha256", plugins.Provided[0].Problem);
        Assert.Contains("jar", plugins.Provided[1].Problem);
    }

    [Fact]
    public void Off_without_a_folder()
    {
        var plugins = new LavalinkPlugins(Options.Create(new LavalinkOptions { BaseAddress = "http://lavalink", Passphrase = "x" }), new HttpClient(new Handler(this)), NullLogger<LavalinkPlugins>.Instance);
        Assert.False(plugins.On);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private sealed class Handler(LavalinkPluginsTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            test._requests++;
            if (!test._served.TryGetValue(request.RequestUri!.ToString(), out var served))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served.Body) };
            response.Content.Headers.ContentType = new(served.Type);
            return Task.FromResult(response);
        }
    }
}
