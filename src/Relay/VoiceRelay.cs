using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace THOBOTTO.Relay;

// A pretend Discord voice server for Lavalink: Lavalink connects here instead of to Discord, plays into it
// as it would into a voice channel, and each Opus frame it sends is handed on (to the helper's own voice
// connection, which can also listen). This hop has no end-to-end encryption (we say so, being the server);
// the transport is AES-256-GCM, as Discord's.
public sealed class VoiceRelay(IConfiguration config, ILogger<VoiceRelay> logger) : BackgroundService
{
    private const string Mode = "aead_aes256_gcm_rtpsize";

    private readonly ConcurrentDictionary<string, RelaySession> _byToken = new();
    private readonly ConcurrentDictionary<uint, RelaySession> _bySsrc = new();
    private UdpClient? _udp;
    private int _nextSsrc = 1000;

    public int Port => config.GetValue("Relay:Port", 2343);

    public int UdpPort => config.GetValue("Relay:UdpPort", 2344);

    // The name Lavalink reaches the bot by (and the certificate is made for).
    public string Host => config["Relay:Host"] is { Length: > 0 } host ? host : "localhost";

    // A session for one player: what to give Lavalink as its voice server, and where its frames go.
    public RelaySession Open(Action<ushort, uint, ReadOnlyMemory<byte>> onFrame)
    {
        var session = new RelaySession(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), (uint)Interlocked.Increment(ref _nextSsrc), RandomNumberGenerator.GetBytes(32), onFrame);
        _byToken[session.Token] = session;
        _bySsrc[session.Ssrc] = session;
        return session;
    }

    public void Close(RelaySession session)
    {
        _byToken.TryRemove(session.Token, out _);
        _bySsrc.TryRemove(session.Ssrc, out _);
    }

    public string Endpoint => $"{Host}:{Port}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The relay is optional: if it can't start, the rest of the bot runs on.
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The voice relay couldn't start; helpers joining through it will fail");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var directory = config["Relay:Directory"] is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "relay");
        var certificate = RelayCertificate.LoadOrCreate(directory, [Host, "localhost", "127.0.0.1"]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(Port, l => l.UseHttps(certificate)));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await ServeAsync(socket, context.RequestAborted);
        });

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort));
        logger.LogInformation("Voice relay on {Endpoint} (UDP {UdpPort}); certificate in {Directory}", Endpoint, UdpPort, directory);
        await Task.WhenAll(app.RunAsync(stoppingToken), ReceiveUdpAsync(_udp, stoppingToken));
    }

    // The voice gateway, v8, as much as Lavalink uses.
    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        var sequence = 0;
        RelaySession? session = null;
        await SendAsync(socket, new() { ["op"] = 8, ["d"] = new JsonObject { ["heartbeat_interval"] = 13_750 } }, ct);
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            using var json = JsonDocument.Parse(message.ToArray());
            message.SetLength(0);
            var root = json.RootElement;
            var op = root.GetProperty("op").GetInt32();
            var d = root.TryGetProperty("d", out var data) ? data : default;
            switch (op)
            {
                case 0 when _byToken.TryGetValue(d.GetProperty("token").GetString()!, out var found):
                    session = found;
                    await SendAsync(socket, new()
                    {
                        ["op"] = 2,
                        ["seq"] = ++sequence,
                        ["d"] = new JsonObject
                        {
                            ["ssrc"] = session.Ssrc,
                            ["ip"] = Host,
                            ["port"] = UdpPort,
                            ["modes"] = new JsonArray(Mode),
                            ["heartbeat_interval"] = 13_750,
                        },
                    }, ct);
                    break;
                case 0:
                    logger.LogWarning("Lavalink identified with an unknown relay token");
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unknown token", ct);
                    return;
                case 1 when session is not null:
                    await SendAsync(socket, new()
                    {
                        ["op"] = 4,
                        ["seq"] = ++sequence,
                        ["d"] = new JsonObject
                        {
                            ["mode"] = Mode,
                            ["secret_key"] = new JsonArray(session.Key.Select(b => (JsonNode)b).ToArray()),
                            ["audio_codec"] = "opus",
                            ["media_session_id"] = session.Token,
                            ["dave_protocol_version"] = 0,
                        },
                    }, ct);
                    break;
                case 3:
                    await SendAsync(socket, new() { ["op"] = 6, ["d"] = new JsonObject { ["t"] = d.TryGetProperty("t", out var t) ? JsonValue.Create(t.GetInt64()) : null } }, ct);
                    break;
            }
        }
    }

    private static Task SendAsync(WebSocket socket, JsonObject message, CancellationToken ct)
        => socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private async Task ReceiveUdpAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(ct);
            }
            catch (SocketException ex)
            {
                // A peer gone away (ICMP unreachable) shows up here; the socket itself is fine.
                logger.LogDebug("Relay UDP: {Message}", ex.Message);
                continue;
            }
            var packet = datagram.Buffer;
            // IP discovery: answer with the address and port we see.
            if (packet.Length == 74 && BinaryPrimitives.ReadUInt16BigEndian(packet) == 1)
            {
                var reply = new byte[74];
                BinaryPrimitives.WriteUInt16BigEndian(reply, 2);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 70);
                packet.AsSpan(4, 4).CopyTo(reply.AsSpan(4));
                Encoding.ASCII.GetBytes(datagram.RemoteEndPoint.Address.ToString()).CopyTo(reply.AsSpan(8));
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(72), (ushort)datagram.RemoteEndPoint.Port);
                await udp.SendAsync(reply, datagram.RemoteEndPoint, ct);
                continue;
            }
            if (packet.Length < 12 + 16 + 4 || (packet[1] & 0x7f) != 0x78 || !_bySsrc.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)), out var session))
                continue;
            if (session.Decrypt(packet) is { } frame)
                session.OnFrame(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)), BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)), frame);
        }
    }
}

public sealed class RelaySession(string token, uint ssrc, byte[] key, Action<ushort, uint, ReadOnlyMemory<byte>> onFrame)
{
    private readonly AesGcm _aes = new(key, 16);

    public string Token { get; } = token;

    public uint Ssrc { get; } = ssrc;

    public byte[] Key { get; } = key;

    public Action<ushort, uint, ReadOnlyMemory<byte>> OnFrame { get; } = onFrame;

    public long Frames { get; private set; }

    // rtpsize: the RTP header (and an extension's first 4 bytes) stay readable as associated data; then the
    // encrypted payload, its 16-byte tag, and a 4-byte counter that, padded with zeros, is the nonce.
    public byte[]? Decrypt(byte[] packet)
    {
        var header = 12 + (packet[0] & 0x0f) * 4;
        var extended = (packet[0] & 0x10) != 0;
        var associated = header + (extended ? 4 : 0);
        var cipherLength = packet.Length - associated - 16 - 4;
        if (cipherLength <= 0)
            return null;
        Span<byte> nonce = stackalloc byte[12];
        packet.AsSpan(packet.Length - 4, 4).CopyTo(nonce);
        var plain = new byte[cipherLength];
        try
        {
            _aes.Decrypt(nonce, packet.AsSpan(associated, cipherLength), packet.AsSpan(packet.Length - 20, 16), plain, packet.AsSpan(0, associated));
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        Frames++;
        // An extension's body is encrypted along with the audio, in front of it.
        var skip = extended ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(header + 2)) * 4 : 0;
        return plain[skip..];
    }
}
