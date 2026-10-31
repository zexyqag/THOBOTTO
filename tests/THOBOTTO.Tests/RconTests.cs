using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

public class RconTests
{
    // A game server's remote control on this machine: the password "secret"; answers in the pieces given. A Source
    // engine server answers the empty packet after a command with it back and then its end marker.
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;

        public FakeServer(string[] answer, bool emptyBeforeLogin = false, bool answersEnd = true)
        {
            _listener.Start();
            _serving = ServeAsync(answer, emptyBeforeLogin, answersEnd);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Commands { get; } = [];

        private async Task ServeAsync(string[] answer, bool emptyBeforeLogin, bool answersEnd)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            try
            {
                while (true)
                {
                    var head = new byte[4];
                    await stream.ReadExactlyAsync(head);
                    var packet = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
                    await stream.ReadExactlyAsync(packet);
                    var (id, type, body) = (BinaryPrimitives.ReadInt32LittleEndian(packet), BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)), Encoding.UTF8.GetString(packet.AsSpan(8, packet.Length - 10)));
                    if (type == 3)
                    {
                        if (emptyBeforeLogin)
                            await SendAsync(stream, id, 0, "");
                        await SendAsync(stream, body == "secret" ? id : -1, 2, "");
                    }
                    else if (type == 2)
                    {
                        Commands.Add(body);
                        foreach (var piece in answer)
                            await SendAsync(stream, id, 0, piece);
                    }
                    else if (answersEnd)
                    {
                        await SendAsync(stream, id, 0, "");
                        await SendAsync(stream, id, 0, "\0\u0001\0\0");
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException)
            {
            }
        }

        private static async Task SendAsync(NetworkStream stream, int id, int type, string body)
        {
            var text = Encoding.UTF8.GetBytes(body);
            var packet = new byte[14 + text.Length];
            BinaryPrimitives.WriteInt32LittleEndian(packet, 10 + text.Length);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
            text.CopyTo(packet, 12);
            await stream.WriteAsync(packet);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Task<string> RunAsync(FakeServer server, string password, string? command, bool multiPacket = false)
        => Rcon.RunAsync("127.0.0.1", server.Port, password, command, multiPacket, TimeSpan.FromSeconds(5), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

    [Fact]
    public async Task A_command_is_run_and_its_answer_read()
    {
        await using var server = new FakeServer(["There are 2 of a max of 20 players online: Ana, Mads"], answersEnd: false);
        Assert.Equal("There are 2 of a max of 20 players online: Ana, Mads", await RunAsync(server, "secret", "list"));
        Assert.Equal(["list"], server.Commands);
    }

    [Fact]
    public async Task A_source_games_long_answer_is_read_to_its_end_marker_past_the_empty_answer_before_the_login()
    {
        // Split where the server's packet was full, mid-word.
        await using var server = new FakeServer(["hostname: Ours\nmap     : de_dust2\npla", "yers : 2 humans"], emptyBeforeLogin: true);
        Assert.Equal("hostname: Ours\nmap     : de_dust2\nplayers : 2 humans", await RunAsync(server, "secret", "status", multiPacket: true));
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_no_command_runs()
    {
        await using var server = new FakeServer(["never"]);
        var refused = await Assert.ThrowsAsync<RconException>(() => RunAsync(server, "guess", "stop"));
        Assert.Equal("It refused the password.", refused.Message);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task Without_a_command_it_only_logs_in()
    {
        await using var server = new FakeServer(["never"]);
        Assert.Equal("", await RunAsync(server, "secret", null));
        Assert.Empty(server.Commands);
    }

    [Theory]
    [InlineData("minecraft", "say [Discord] Valheim night is starting now!")]
    [InlineData("teamfortress2", "say [Discord] Valheim night is starting now!")]
    [InlineData("projectzomboid", "servermsg \"[Discord] Valheim night is starting now!\"")]
    [InlineData("sdtd", "say \"[Discord] Valheim night is starting now!\"")]
    [InlineData("asa", "ServerChat [Discord] Valheim night is starting now!")]
    [InlineData("factorio", "[Discord] Valheim night is starting now!")]
    [InlineData("squad", "AdminBroadcast [Discord] Valheim night is starting now!")]
    [InlineData("vrising", "announce [Discord] Valheim night is starting now!")]
    [InlineData("rust", "say [Discord] Valheim night is starting now!")]
    [InlineData("palworld", "announce [Discord] Valheim night is starting now!")]
    public void Each_game_is_told_its_own_way(string game, string expected)
        => Assert.Equal(expected, GameControls.Say(game, "[Discord] Valheim night is starting now!"));

    [Fact]
    public void A_game_the_bot_cant_talk_to_gets_nothing_and_quotes_dont_break_a_quoted_message()
    {
        Assert.Null(GameControls.Say("valheim", "hi"));
        Assert.Equal("servermsg \"Ana's 'big' night\"", GameControls.Say("projectzomboid", "Ana's \"big\" night"));
    }

    [Fact]
    public void Each_games_whitelist_takes_what_it_knows_players_by()
    {
        var minecraft = GameControls.Whitelist("minecraft")!;
        Assert.Equal((WhitelistBy.Name, "whitelist add Sprig_1", "whitelist remove Sprig_1"), (minecraft.By, minecraft.Add("Sprig_1", null), minecraft.Remove("Sprig_1")));
        Assert.True(minecraft.Took("Added Sprig_1 to the whitelist"));
        Assert.True(minecraft.Took("Player is already whitelisted"));
        Assert.False(minecraft.Took("That player does not exist"));
        Assert.Matches(minecraft.Valid!, "Sprig_1");
        Assert.DoesNotMatch(minecraft.Valid!, "two words");

        var zomboid = GameControls.Whitelist("projectzomboid")!;
        Assert.Equal((WhitelistBy.Account, "adduser \"Ana\" \"hunter2\"", "removeuserfromwhitelist \"Ana\""), (zomboid.By, zomboid.Add("Ana", "hunter2"), zomboid.Remove("Ana")));
        Assert.True(zomboid.Took("User Ana created with the password hunter2"));
        Assert.False(zomboid.Took("A user with this name already exists"));
        Assert.DoesNotMatch(zomboid.Valid!, "a\"quote");

        var sevenDays = GameControls.Whitelist("sdtd")!;
        Assert.Equal((WhitelistBy.Steam, "whitelist add Steam_76561198000000001"), (sevenDays.By, sevenDays.Add("Steam_76561198000000001", null)));
        Assert.Null(GameControls.Whitelist("counterstrike2"));
    }

    [Theory]
    [InlineData("sdtd", RemoteConsole.Telnet)]
    [InlineData("rust", RemoteConsole.WebRcon)]
    [InlineData("palworld", RemoteConsole.PalworldApi)]
    [InlineData("minecraft", RemoteConsole.Rcon)]
    [InlineData("somethingnew", RemoteConsole.Rcon)]
    public void Each_game_has_its_console_and_the_rest_rcon(string game, RemoteConsole console)
        => Assert.Equal(console, GameControls.Console(game));

    [Fact]
    public void An_answer_keeps_inside_its_code_block()
    {
        Assert.DoesNotContain("```", ServerCommands.Fence("before ``` after"), StringComparison.Ordinal);
        Assert.Equal(1900, ServerCommands.Fence(new string('x', 5000)).Length);
    }
}
