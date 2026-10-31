using System.Net;
using System.Net.Sockets;

using CoreRCON;

namespace THOBOTTO.GameServers;

public sealed class RconException(string message) : Exception(message);

// Source RCON (Minecraft, Project Zomboid, ARK, the Source engine's games) through CoreRCON, which players have tried
// against those games' quirks: one connection for one command, or none, to see the login take. multiPacket: the Source
// engine's way of marking where a long answer ends, which other games don't answer.
public static class Rcon
{
    public static async Task<string> RunAsync(string host, int port, string password, string? command, bool multiPacket, TimeSpan wait, CancellationToken ct)
    {
        // CoreRCON connects over IPv4 only.
        var address = (await Dns.GetHostAddressesAsync(host, ct)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new RconException($"{host} has no IPv4 address.");
        using var rcon = new RCON(address, (ushort)port, password, (uint)wait.TotalMilliseconds, multiPacket);
        try
        {
            await rcon.ConnectAsync().WaitAsync(ct);
            return command is null ? "" : await rcon.SendCommandAsync(command).WaitAsync(ct);
        }
        catch (AuthenticationException)
        {
            throw new RconException("It refused the password.");
        }
        catch (TimeoutException)
        {
            throw new RconException("It didn't answer in time.");
        }
    }
}
