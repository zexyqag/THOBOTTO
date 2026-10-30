using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using THOBOTTO.Integrations;

namespace THOBOTTO.Helpers;

// Lavalink's plugins, kept by the bot: the jars named in the settings are fetched into a folder Lavalink mounts as
// its plugins folder, each checked against its sum, and nothing else is left in there. Lavalink then declares no
// plugins of its own, so its start never asks the repository, and a repository outage can only hold up a version
// bump. Off when no folder is set. The container counts as healthy once this was tried, fetched or not, so Lavalink
// (which waits for that) starts with the folder as complete as it could be made.
public sealed class LavalinkPlugins(IOptions<LavalinkOptions> options, [FromKeyedServices(LavalinkPlugins.HttpClientName)] HttpClient http, ILogger<LavalinkPlugins> logger) : IHostedService
{
    public const string HttpClientName = "LavalinkPlugins";

    private readonly CancellationTokenSource _stopping = new();

    public bool On => !string.IsNullOrEmpty(options.Value.PluginsDirectory);

    public bool Tried { get; private set; }

    // What the last pass left in the folder, in the settings' order.
    public IReadOnlyList<ProvidedPlugin> Provided { get; private set; } = [];

    public Task StartAsync(CancellationToken ct)
    {
        if (On)
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProvideAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
        else
            Tried = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    // Keeps a jar whose sum matches, fetches one that is missing or wrong, and removes every other file.
    public async Task ProvideAsync(CancellationToken ct)
    {
        var directory = options.Value.PluginsDirectory!;
        var wanted = options.Value.Plugins.Where(p => !string.IsNullOrEmpty(p.Value.Url)).ToList();
        var provided = new List<ProvidedPlugin>();
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var (name, source) in wanted)
                provided.Add(await ProvideOneAsync(name, source, directory, ct));
            var keep = provided.Select(p => p.File).ToHashSet();
            foreach (var path in Directory.EnumerateFiles(directory).Where(p => !keep.Contains(Path.GetFileName(p))))
            {
                File.Delete(path);
                logger.LogInformation("Removed {File} from Lavalink's plugins folder: not among the plugins kept", Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Lavalink's plugins folder {Directory} can't be used: {Message}", directory, ex.Message);
            provided = [.. wanted.Select(p => new ProvidedPlugin(p.Key, FileOf(p.Value.Url!), $"The folder can't be used: {ex.Message}"))];
        }
        finally
        {
            Provided = provided;
            Tried = true;
        }
    }

    private async Task<ProvidedPlugin> ProvideOneAsync(string name, PluginSource source, string directory, CancellationToken ct)
    {
        var file = FileOf(source.Url!);
        var sha256 = source.Sha256?.ToLowerInvariant();
        if (!file.EndsWith(".jar", StringComparison.Ordinal))
            return new(name, file, "The address doesn't name a jar file.");
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            return new(name, file, "The sha256 setting isn't a 64-digit sum.");
        var path = Path.Combine(directory, file);
        try
        {
            if (File.Exists(path))
            {
                if (await Sha256Async(path, ct) == sha256)
                    return new(name, file, null);
                File.Delete(path);
                logger.LogWarning("{File} in Lavalink's plugins folder isn't the file expected and was removed", file);
            }
            logger.LogInformation("Fetching {File} for Lavalink from {Url}", file, source.Url);
            using var response = await http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType == "text/html")
                return new(name, file, "The address answered a web page instead of the file; the repository may be down.");
            await VerifiedFile.SaveAsync(await response.Content.ReadAsStreamAsync(ct), path, sha256, _ => { }, ct);
            logger.LogInformation("{File} fetched for Lavalink", file);
            return new(name, file, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or OperationCanceledException)
        {
            logger.LogWarning("{File} couldn't be fetched for Lavalink: {Message}", file, ex.Message);
            return new(name, file, ex.Message);
        }
    }

    private static string FileOf(string url) => Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url);

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}

// A plugin the bot keeps for Lavalink: in the folder when Problem is null, otherwise why it isn't.
public sealed record ProvidedPlugin(string Name, string File, string? Problem)
{
    // The version in the jar's name, as Lavalink reports it.
    public string? Version => File.StartsWith(Name + "-", StringComparison.Ordinal) && File.EndsWith(".jar", StringComparison.Ordinal) ? File[(Name.Length + 1)..^4] : null;
}
