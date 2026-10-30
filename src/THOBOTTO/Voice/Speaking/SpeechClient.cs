using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Helpers;

namespace THOBOTTO.Voice.Speaking;

// An OpenAI-style speech service: Kokoro-FastAPI on the stack, or a cloud one. Raw PCM comes back at 24 kHz.
public sealed class SpeechClient([FromKeyedServices(nameof(SpeechClient))] HttpClient http, ILogger<SpeechClient> logger)
{
    private const int Rate = 24_000;

    // OpenAI's own voices, for a cloud service that doesn't list them.
    public static readonly string[] OpenAiVoices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    public static IReadOnlyList<string> ParseVoices(JsonElement listed)
        => listed.GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("id").GetString()!).Order().ToList();

    public async Task<Spoken?> SayAsync(string url, string? key, string model, string text, string voice, double speed, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["input"] = text, ["voice"] = voice, ["response_format"] = "pcm" };
        if (Math.Abs(speed - 1) > 1e-6)
            body["speed"] = Math.Round(speed, 3);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/audio/speech") { Content = JsonContent.Create(body) };
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The speech service said {Status}: {Body}", (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                return null;
            }
            return new(Pcm.FromBytes(await response.Content.ReadAsByteArrayAsync(ct)), Rate);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Reaching the speech service failed: {Message}", ex.Message);
            return null;
        }
    }

    // The voices the service lists ({"voices": [{"id": …}]}), or null when it lists none.
    public async Task<IReadOnlyList<string>?> VoicesAsync(string url)
    {
        try
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync($"{url.TrimEnd('/')}/audio/voices"));
            return ParseVoices(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Listing the speech service's voices failed: {Message}", ex.Message);
            return null;
        }
    }
}
