using System.Net;
using System.Net.Sockets;
using System.Text;

using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

public class TelnetTests
{
    // 7 Days to Die's console on this machine, as it talks: the password asked first (unless it has none), a greeting,
    // then each command's lines after its note of running it.
    private sealed class FakeConsole : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;

        public FakeConsole(string? password)
        {
            _listener.Start();
            _serving = ServeAsync(password);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Commands { get; } = [];

        private async Task ServeAsync(string? password)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            async Task SendAsync(string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")));
            try
            {
                if (password is not null)
                {
                    await SendAsync("Please enter password:\n");
                    if (await reader.ReadLineAsync() != password)
                    {
                        await SendAsync("Password incorrect, please enter password:\n");
                        return;
                    }
                    await SendAsync("Logon successful.\n");
                    // A busy server's greeting comes a moment after.
                    await Task.Delay(700);
                }
                await SendAsync("\n\n*** Connected with 7DTD server.\n*** Server version: V 1.2 (b27)\n\n");
                while (await reader.ReadLineAsync() is { } line && line != "exit")
                {
                    Commands.Add(line);
                    await SendAsync($"2026-10-31T10:00:00 123.456 INF Executing command '{line}' by Telnet from 127.0.0.1:50000\n");
                    await SendAsync(line == "lp" ? "0. id=171, Ana, pos=(1, 2, 3)\nTotal of 1 in the game\n" : "");
                }
            }
            catch (IOException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Task<string> RunAsync(FakeConsole console, string password, string? command)
        => Telnet.RunAsync("127.0.0.1", console.Port, password, command, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

    [Fact]
    public async Task A_command_is_run_after_the_password_and_its_lines_read()
    {
        await using var console = new FakeConsole("secret");
        Assert.Equal("0. id=171, Ana, pos=(1, 2, 3)\nTotal of 1 in the game", await RunAsync(console, "secret", "lp"));
        Assert.Equal(["lp"], console.Commands);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        await using var console = new FakeConsole("secret");
        var refused = await Assert.ThrowsAsync<RconException>(() => RunAsync(console, "guess", "lp"));
        Assert.Equal("It refused the password.", refused.Message);
    }

    [Fact]
    public async Task A_console_without_a_password_takes_commands_at_once()
    {
        await using var console = new FakeConsole(null);
        Assert.Equal("Total of 1 in the game", (await RunAsync(console, "", "lp")).Split('\n')[^1]);
    }

    [Fact]
    public async Task Without_a_command_it_only_logs_in()
    {
        await using var console = new FakeConsole("secret");
        Assert.Equal("", await RunAsync(console, "secret", null));
        Assert.Empty(console.Commands);
    }
}
