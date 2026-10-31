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
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{host}:{port}/{Uri.EscapeDataString(password)}"), ct);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.NotAWebSocket)
        {
            // A wrong password is answered without the upgrade.
            throw new RconException("It refused the password.");
        }
        if (command is null)
            return "";
        var request = JsonSerializer.SerializeToUtf8Bytes(new { Identifier = CommandId, Message = command, Name = "THOBOTTO" });
        await socket.SendAsync(request, WebSocketMessageType.Text, true, ct);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new RconException("It closed the connection.");
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);
            using var answer = JsonDocument.Parse(message.ToArray());
            if (answer.RootElement.TryGetProperty("Identifier", out var id) && id.GetInt32() == CommandId)
                return answer.RootElement.TryGetProperty("Message", out var text) ? text.GetString() ?? "" : "";
        }
    }
}
