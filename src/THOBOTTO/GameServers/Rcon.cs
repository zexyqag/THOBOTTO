using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace THOBOTTO.GameServers;

public sealed class RconException(string message) : Exception(message);

// The Source RCON protocol, which Minecraft, Project Zomboid, Palworld, ARK, Counter-Strike and Factorio speak: one
// connection for one command (or none, to see the login take).
public static class Rcon
{
    private const int Response = 0, Command = 2, AuthAnswer = 2, Auth = 3;
    private const int LoginId = 1, CommandId = 2, EndId = 3, Refused = -1;
    private const int Largest = 1 << 16;

    // After the first packet of an answer, the server going this quiet ends it: not every game answers the empty
    // packet sent after a command, which marks the end where they do.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly record struct Packet(int Id, int Type, string Body);

    public static async Task<string> RunAsync(string host, int port, string password, string? command, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);
        var stream = tcp.GetStream();
        await WriteAsync(stream, LoginId, Auth, password, ct);
        // Source games send an empty answer before the one to the login.
        Packet login;
        do
            login = await ReadAsync(stream, ct);
        while (login.Type != AuthAnswer);
        if (login.Id == Refused)
            throw new RconException("It refused the password.");
        if (command is null)
            return "";

        await WriteAsync(stream, CommandId, Command, command, ct);
        await WriteAsync(stream, EndId, Response, "", ct);
        var answer = new StringBuilder();
        var heard = false;
        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (heard)
                quiet.CancelAfter(Quiet);
            Packet packet;
            try
            {
                packet = await ReadAsync(stream, quiet.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
            if (packet.Id == EndId)
                break;
            if (packet.Id == CommandId)
                answer.Append(packet.Body);
            heard = true;
        }
        return answer.ToString();
    }

    private static async Task WriteAsync(NetworkStream stream, int id, int type, string body, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[14 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, 10 + text.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet, 12);
        await stream.WriteAsync(packet, ct);
    }

    private static async Task<Packet> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (size is < 10 or > Largest)
            throw new RconException("Its answer isn't RCON.");
        var packet = new byte[size];
        await stream.ReadExactlyAsync(packet, ct);
        return new(
            BinaryPrimitives.ReadInt32LittleEndian(packet),
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)),
            Encoding.UTF8.GetString(packet.AsSpan(8, size - 8).TrimEnd((byte)0)));
    }
}
