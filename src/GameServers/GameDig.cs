using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace THOBOTTO.GameServers;

public sealed class GameDigOptions
{
    // Folder holding GameDig's node_modules.
    public required string Path { get; set; }
}

public sealed record GameDigGame(string Id, string Name, string? OldId);

public sealed record ServerStatus(string? Name, string? Map, int Players, int MaxPlayers, IReadOnlyList<string> PlayerNames, bool Password);

// Runs GameDig's CLI under Node. A failed query returns null.
public sealed class GameDig(IOptions<GameDigOptions> options, ILogger<GameDig> logger)
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    private const string ListGamesScript = """
        import { pathToFileURL } from "node:url";
        const { games } = await import(pathToFileURL(process.argv[1] + "/node_modules/gamedig/lib/index.js"));
        console.log(JSON.stringify(Object.entries(games).map(([id, g]) => ({ id, name: g.name, oldId: g.extra?.old_id ?? null }))));
        """;

    private Task<IReadOnlyList<GameDigGame>>? _games;

    public Task<IReadOnlyList<GameDigGame>> GetGamesAsync()
        => LazyInitializer.EnsureInitialized(ref _games, () => ListGamesAsync(options.Value.Path));

    public async Task<GameDigGame?> FindGameAsync(string id)
    {
        var games = await GetGamesAsync();
        return games.FirstOrDefault(g => g.Id == id) ?? games.FirstOrDefault(g => g.OldId == id);
    }

    public async Task<ServerStatus?> QueryAsync(string game, string host, int? port, CancellationToken ct)
    {
        if (!await IsPublicAsync(host, ct))
        {
            logger.LogWarning("Refusing to query {Host}: it isn't a public address", host);
            return null;
        }

        var cli = System.IO.Path.Combine(options.Value.Path, "node_modules", "gamedig", "bin", "gamedig.js");
        using var output = await RunNodeAsync([cli, "--type", game, "--checkOldIDs", port is null ? host : $"{host}:{port}"], QueryTimeout, ct);
        if (output is null)
            return null;

        var root = output.RootElement;
        if (root.TryGetProperty("error", out _))
            return null;

        var names = root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array
            ? players.EnumerateArray()
                .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList()
            : [];

        return new(
            String(root, "name"),
            String(root, "map"),
            Int(root, "numplayers") ?? names.Count,
            Int(root, "maxplayers") ?? 0,
            names,
            root.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True);
    }

    // Hosts are user input: only accept names and addresses, never anything the CLI could read as a flag.
    public static bool IsValidHost(string host)
        => !host.StartsWith('-') && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;

    // Members add servers, so keep them from pointing the bot at its own network.
    private static async Task<bool> IsPublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
            return false;

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127
            || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
            || (b[0] == 192 && b[1] == 168)
            || b[0] >= 224);
    }

    private async Task<IReadOnlyList<GameDigGame>> ListGamesAsync(string path)
    {
        using var output = await RunNodeAsync(["--input-type=module", "-e", ListGamesScript, path], TimeSpan.FromSeconds(30), CancellationToken.None)
            ?? throw new InvalidOperationException($"Listing GameDig's games from {path} failed");

        return output.RootElement.EnumerateArray()
            .Select(g => new GameDigGame(String(g, "id")!, String(g, "name")!, String(g, "oldId")))
            .ToList();
    }

    private async Task<JsonDocument?> RunNodeAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Node exited with {ExitCode}: {Error}", process.ExitCode, await stderr);
                return null;
            }

            return JsonDocument.Parse(await stdout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Node timed out after {Timeout}", timeout);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Node printed something other than JSON");
            return null;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var i) ? i : null;
}
