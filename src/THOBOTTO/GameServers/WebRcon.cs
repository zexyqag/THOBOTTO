using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace THOBOTTO.GameServers;

// Rust's console: a WebSocket at ws://host:port/password, a command as {"Identifier", "Message", "Name"} and its answer
// the message with the same identifier (the server's log comes in between as 0). No library for it is in wide use
// (the few on NuGet have a few thousand downloads), and it's this small.
public static class WebRcon
{
    private const int CommandId = 1001;

    public static async Task<string> RunAsync(string host, int port, string password, string? command, CancellationToken ct)
    {
        using var socket = await ConnectAsync(host, port, password, ct);
        if (command is null)
            return "";
        var request = JsonSerializer.SerializeToUtf8Bytes(new { Identifier = CommandId, Message = command, Name = "THOBOTTO" });
        await socket.SendAsync(request, WebSocketMessageType.Text, true, ct);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var answer = await ReceiveAsync(socket, buffer, ct) ?? throw new RconException("It closed the connection.");
            if (answer.RootElement.TryGetProperty("Identifier", out var id) && id.GetInt32() == CommandId)
                return answer.RootElement.TryGetProperty("Message", out var text) ? text.GetString() ?? "" : "";
        }
    }

    // Kept open, each message it sends (its log, its chat) handed on, until it closes or ct ends.
    public static async Task ListenAsync(string host, int port, string password, Func<JsonElement, Task> message, CancellationToken ct)
    {
        using var socket = await ConnectAsync(host, port, password, ct);
        var buffer = new byte[64 * 1024];
        while (await ReceiveAsync(socket, buffer, ct) is { } received)
        {
            using (received)
                await message(received.RootElement);
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(string host, int port, string password, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{host}:{port}/{Uri.EscapeDataString(password)}"), ct);
            return socket;
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.NotAWebSocket)
        {
            socket.Dispose();
            // A wrong password is answered without the upgrade.
            throw new RconException("It refused the password.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A whole message, or null once it closes.
    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return JsonDocument.Parse(message.ToArray());
    }
}
