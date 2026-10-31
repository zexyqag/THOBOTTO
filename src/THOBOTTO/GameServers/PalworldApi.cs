using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace THOBOTTO.GameServers;

// Palworld's REST API (its RCON is deprecated): http://host:port/v1/api, Basic with the user admin and the server's
// AdminPassword. The console takes its operations as words: info, players, settings, metrics, save, stop,
// announce <text>, kick <user id> [why], ban <user id> [why], unban <user id>, shutdown <seconds> [message].
public static class PalworldApi
{
    public const string Operations = "info, players, settings, metrics, save, stop, announce <text>, kick <user id> [why], ban <user id> [why], unban <user id>, shutdown <seconds> [message]";

    public static async Task<string> RunAsync(HttpClient http, string host, int port, string password, string? command, CancellationToken ct)
    {
        var (method, path, body) = Request(command ?? "info") ?? throw new RconException($"Palworld's console takes: {Operations}.");
        using var request = new HttpRequestMessage(method, $"http://{host}:{port}/v1/api/{path}") { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{password}")));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RconException("It refused the password.");
        if (!response.IsSuccessStatusCode)
            throw new RconException($"It answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (command is null)
            return "";
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(json.RootElement, Indented);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The call a console line stands for; null for one it doesn't know.
    public static (HttpMethod Method, string Path, object? Body)? Request(string command)
    {
        var words = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return null;
        var rest = words.Length > 1 ? words[1] : null;
        var (first, why) = rest?.Split(' ', 2, StringSplitOptions.TrimEntries) is [var a, var b] ? (a, b) : (rest, null);
        return words[0].ToLowerInvariant() switch
        {
            "info" or "players" or "settings" or "metrics" when rest is null => (HttpMethod.Get, words[0].ToLowerInvariant(), null),
            "save" or "stop" when rest is null => (HttpMethod.Post, words[0].ToLowerInvariant(), null),
            "announce" when rest is not null => (HttpMethod.Post, "announce", new { message = rest }),
            "kick" or "ban" when first is not null => (HttpMethod.Post, words[0].ToLowerInvariant(), new { userid = first, message = why ?? "" }),
            "unban" when first is not null && why is null => (HttpMethod.Post, "unban", new { userid = first }),
            "shutdown" when int.TryParse(first, out var seconds) => (HttpMethod.Post, "shutdown", new { waittime = seconds, message = why ?? "" }),
            _ => null,
        };
    }
}
