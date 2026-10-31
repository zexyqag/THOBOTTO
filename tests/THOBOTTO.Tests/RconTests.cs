using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

public class RconTests
{
    // A game server's remote control on this machine: the password "secret"; answers in the pieces given.
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
                        await SendAsync(stream, id, 0, "Unknown request 0");
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

    private static Task<string> RunAsync(FakeServer server, string password, string? command)
        => Rcon.RunAsync("127.0.0.1", server.Port, password, command, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

    [Fact]
    public async Task A_command_is_run_and_its_answer_read_to_the_end_marker()
    {
        await using var server = new FakeServer(["There are 2 of a max of 20 players online: ", "Ana, Mads"]);
        Assert.Equal("There are 2 of a max of 20 players online: Ana, Mads", await RunAsync(server, "secret", "list"));
        Assert.Equal(["list"], server.Commands);
    }

    [Fact]
    public async Task A_source_games_empty_answer_before_the_login_is_passed_over()
    {
        await using var server = new FakeServer(["hostname: Ours"], emptyBeforeLogin: true);
        Assert.Equal("hostname: Ours", await RunAsync(server, "secret", "status"));
    }

    [Fact]
    public async Task A_game_that_never_answers_the_end_marker_ends_by_going_quiet()
    {
        await using var server = new FakeServer(["Players connected (1):", "\n-Ana"], answersEnd: false);
        Assert.Equal("Players connected (1):\n-Ana", await RunAsync(server, "secret", "players"));
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
    [InlineData("projectzomboid", "servermsg \"[Discord] Valheim night is starting now!\"")]
    [InlineData("asa", "ServerChat [Discord] Valheim night is starting now!")]
    [InlineData("factorio", "[Discord] Valheim night is starting now!")]
    public void Each_game_is_told_its_own_way(string game, string expected)
        => Assert.Equal(expected, ServerControl.Say(game, "[Discord] Valheim night is starting now!"));

    [Fact]
    public void A_game_the_bot_cant_talk_to_gets_nothing_and_quotes_dont_break_zomboids_message()
    {
        Assert.Null(ServerControl.Say("valheim", "hi"));
        Assert.Equal("servermsg \"Ana's 'big' night\"", ServerControl.Say("projectzomboid", "Ana's \"big\" night"));
    }

    [Fact]
    public void An_answer_keeps_inside_its_code_block()
    {
        Assert.DoesNotContain("```", ServerCommands.Fence("before ``` after"), StringComparison.Ordinal);
        Assert.Equal(1900, ServerCommands.Fence(new string('x', 5000)).Length);
    }
}
