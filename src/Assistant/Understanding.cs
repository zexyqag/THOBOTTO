using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Integrations;

namespace THOBOTTO.Assistant;

// A language model on the stack (llama.cpp's server, or another with an OpenAI-style API) reading a sentence
// for one of the voice actions, when the set phrases didn't match. Its answer is held to the actions' shapes,
// so it can only pick an action there is, with arguments that fit.
public sealed class Understanding(IntegrationStore store, ILogger<Understanding> logger)
{
    public const string Nothing = "none";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public bool On => Url is not null;

    private string? Url => store.Get(IntegrationStore.UnderstandingUrl)?.TrimEnd('/');

    // The action and its arguments; null when it's nothing it can do, or the model can't be reached.
    // Context: who speaks, who's in the call, who was just mentioned.
    public async Task<(VoiceAction Action, JsonElement Args)?> ReadAsync(string said, string context, IReadOnlyList<VoiceAction> actions, CancellationToken ct = default)
    {
        if (Url is not { } url)
            return null;
        var options = actions.Select(a => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["const"] = a.Name },
                ["args"] = new JsonObject { ["type"] = "object", ["properties"] = a.Properties.DeepClone(), ["required"] = new JsonArray([.. a.Required.Select(r => JsonValue.Create(r))]), ["additionalProperties"] = false },
            },
            ["required"] = new JsonArray("action", "args"),
            ["additionalProperties"] = false,
        }).Append(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["action"] = new JsonObject { ["const"] = Nothing } },
            ["required"] = new JsonArray("action"),
            ["additionalProperties"] = false,
        });
        // The same instructions every time (only the last message changes), so the server reuses its work on them.
        var instructions = "You turn what a Discord member said to a voice helper bot into one action. Speech to text mishears words, "
            + "so read for what they meant (\"qa dude\" may be \"queue Hey Jude\"). For a person, use the name of whoever they "
            + "most likely meant from those listed (\"mardi\" may be \"Marty\"). "
            + $"Actions:\n{string.Join('\n', actions.Select(a => $"- {a.Name}: {a.Description}"))}\n- {Nothing}: anything else.\nAnswer with the action as JSON.";
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = $"{context}\nSaid: {said}" }),
            ["temperature"] = 0,
            ["max_tokens"] = 120,
            ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "action", ["schema"] = new JsonObject { ["oneOf"] = new JsonArray([.. options]) } } },
        };
        try
        {
            using var response = await Http.PostAsJsonAsync($"{url}/v1/chat/completions", body, ct);
            response.EnsureSuccessStatusCode();
            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var content = reply.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
            var picked = JsonDocument.Parse(content).RootElement;
            var name = picked.GetProperty("action").GetString();
            return actions.FirstOrDefault(a => a.Name == name) is { } action
                ? (action, picked.TryGetProperty("args", out var args) ? args.Clone() : JsonDocument.Parse("{}").RootElement)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Understanding a sentence failed: {Message}", ex.Message);
            return null;
        }
    }

    // Whether the server answers, for the Integrations page.
    public async Task<bool> ReachableAsync()
    {
        if (Url is not { } url)
            return false;
        try
        {
            using var response = await Http.GetAsync($"{url}/health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
