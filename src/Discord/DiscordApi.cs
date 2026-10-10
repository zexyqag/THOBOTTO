using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace THOBOTTO.Discord;

public sealed record SoundboardSound(ulong Id, string Name, ulong? UserId);

// Discord's API where NetCord has nothing for it yet: pausing invites, and soundboard sounds.
public sealed class DiscordApi(IConfiguration config, ILogger<DiscordApi> logger)
{
    private const string Api = "https://discord.com/api/v10";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Discord's "pause invites", for at most a day (until null: resume).
    public async Task<bool> PauseInvitesAsync(ulong guildId, DateTimeOffset? until)
        => await SendAsync(HttpMethod.Put, $"/guilds/{guildId}/incident-actions", new JsonObject { ["invites_disabled_until"] = until?.ToString("O") }) is not null;

    public async Task<IReadOnlyList<SoundboardSound>?> SoundsAsync(ulong guildId)
    {
        using var reply = await SendAsync(HttpMethod.Get, $"/guilds/{guildId}/soundboard-sounds", null);
        return reply?.RootElement.GetProperty("items").EnumerateArray().Select(Sound).ToList();
    }

    // MP3 or OGG, at most 5.2 s and 512 KB.
    public async Task<SoundboardSound?> AddSoundAsync(ulong guildId, string name, byte[] audio, string fileName)
    {
        var type = fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : "audio/mpeg";
        using var reply = await SendAsync(HttpMethod.Post, $"/guilds/{guildId}/soundboard-sounds",
            new JsonObject { ["name"] = name, ["sound"] = $"data:{type};base64,{Convert.ToBase64String(audio)}" });
        return reply is null ? null : Sound(reply.RootElement);
    }

    public async Task<bool> RemoveSoundAsync(ulong guildId, ulong soundId)
        => await SendAsync(HttpMethod.Delete, $"/guilds/{guildId}/soundboard-sounds/{soundId}", null) is not null;

    // A soundboard sound's file (from Discord's CDN).
    public async Task<byte[]?> SoundFileAsync(ulong soundId)
    {
        try
        {
            return await Http.GetByteArrayAsync($"https://cdn.discordapp.com/soundboard-sounds/{soundId}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Fetching soundboard sound {SoundId} failed: {Message}", soundId, ex.Message);
            return null;
        }
    }

    private static SoundboardSound Sound(JsonElement s) => new(
        ulong.Parse(s.GetProperty("sound_id").GetString()!),
        s.GetProperty("name").GetString()!,
        s.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? ulong.Parse(user.GetProperty("id").GetString()!) : null);

    // The reply (an empty document for "no content"), or null when Discord refused.
    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, Api + path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", config["Discord:Token"]);
        try
        {
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Discord said {Status} to {Method} {Path}: {Body}", (int)response.StatusCode, method, path, text);
                return null;
            }
            return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("{Method} {Path} failed: {Message}", method, path, ex.Message);
            return null;
        }
    }
}
