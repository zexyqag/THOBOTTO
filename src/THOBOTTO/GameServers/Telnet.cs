using System.Net.Sockets;
using System.Text;

namespace THOBOTTO.GameServers;

// 7 Days to Die's console: plain text over TCP, a password asked first (none when it has no password). It streams the
// server's log too, so an answer is what follows a command until it goes quiet.
public sealed class Telnet
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly NetworkStream _stream;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly byte[] _buffer = new byte[4096];
    private readonly char[] _chars = new char[4096];

    private Telnet(NetworkStream stream) => _stream = stream;

    public static async Task<string> RunAsync(string host, int port, string password, string? command, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);
        var telnet = new Telnet(tcp.GetStream());
        if ((await telnet.ReadAsync(t => t.Contains("password", StringComparison.OrdinalIgnoreCase), Prompt, ct)).Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            await telnet.WriteLineAsync(password, ct);
            var logon = await telnet.ReadAsync(t => t.Contains("successful", StringComparison.OrdinalIgnoreCase) || t.Contains("incorrect", StringComparison.OrdinalIgnoreCase), Prompt, ct);
            if (!logon.Contains("successful", StringComparison.OrdinalIgnoreCase))
                throw new RconException("It refused the password.");
        }
        // The greeting after logging in.
        await telnet.ReadAsync(_ => false, Quiet, ct, quiet: true);
        var answer = "";
        if (command is not null)
        {
            await telnet.WriteLineAsync(command, ct);
            answer = Answer(await telnet.ReadAsync(_ => false, Prompt, ct, quiet: true));
        }
        await telnet.WriteLineAsync("exit", ct);
        return answer;
    }

    // Its lines, without the server's note of running the command.
    public static string Answer(string text)
        => string.Join('\n', text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.Contains("Executing command '", StringComparison.Ordinal)));

    private async Task WriteLineAsync(string line, CancellationToken ct)
        => await _stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct);

    // What comes until done says so, or for at most wait; quiet: until nothing has come for a moment (after the first
    // of it, within wait).
    private async Task<string> ReadAsync(Func<string, bool> done, TimeSpan wait, CancellationToken ct, bool quiet = false)
    {
        var text = new StringBuilder();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(wait);
        try
        {
            while (!done(text.ToString()))
            {
                using var pause = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                if (quiet && text.Length > 0)
                    pause.CancelAfter(Quiet);
                var read = await _stream.ReadAsync(_buffer, pause.Token);
                if (read == 0)
                    break;
                text.Append(_chars, 0, _decoder.GetChars(_buffer, 0, read, _chars, 0));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        return text.ToString();
    }
}
