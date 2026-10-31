using System.Net.Sockets;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Events;

namespace THOBOTTO.GameServers;

// Game servers' remote control: commands from the console, and a game's sessions announced in it.
public sealed class ServerControl(IDbContextFactory<BotDbContext> dbFactory, ServerDetails details, ILogger<ServerControl> logger) : IEventDecorator
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // How each game tells everyone playing something; null where the bot doesn't know.
    public static string? Say(string game, string text) => game switch
    {
        "minecraft" or "counterstrike2" or "csgo" => $"say {text}",
        "projectzomboid" => $"servermsg \"{text.Replace('"', '\'')}\"",
        "ase" or "asa" => $"ServerChat {text}",
        // Anything that isn't a command is chat.
        "factorio" => text.TrimStart('/'),
        _ => null,
    };

    // Its answer, or why there's none; without a command, whether it lets the bot in.
    public async Task<(string? Answer, string? Problem)> RunAsync(GameServer server, string? command, CancellationToken ct = default)
    {
        if (server.RconAddress is not { } address || details.RconPasswordOf(server) is not { } password)
            return (null, "Remote control isn't set up for that server: its RCON address and password go on the Game servers page.");
        if (!ServerAddress.TryParse(address, out var host, out var port) || port is not { } rconPort)
            return (null, "Its RCON address isn't host:port.");
        if (!server.RconInside && !await PublicAddress.IsPublicAsync(host, ct))
            return (null, "Its RCON address is inside the bot's own network, which only the bot's owner can set.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Wait);
        try
        {
            return (await Rcon.RunAsync(host, rconPort, password, command, timeout.Token), null);
        }
        catch (RconException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "It didn't answer in time.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return (null, $"It couldn't be reached: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<string>> LinesAsync(Event e) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task PostedAsync(Event e) => Task.CompletedTask;

    // A session's reminder and start, said in its game where the game's server has remote control.
    public async Task AnnouncedAsync(Event e, string said)
    {
        if (e.GameId is not { } gameId)
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Games.FindAsync(gameId) is not { ServerId: { } serverId }
            || await db.GameServers.FindAsync(serverId) is not { RconAddress: not null, AnnounceSessions: true } server
            || Say(server.Game, $"[Discord] {said}") is not { } command)
            return;
        using var _ = logger.BeginScope(DebugLog.Guild(e.GuildId));
        if ((await RunAsync(server, command)).Problem is { } problem)
            logger.LogWarning("Announcing {Event} in {Server} failed: {Problem}", e.Title, server.Name ?? server.Address, problem);
    }
}
