using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Helpers;
using THOBOTTO.Listening;
using THOBOTTO.Music;

namespace THOBOTTO.Assistant;

// Who said it, where, and the music there (if any).
public sealed record VoiceRequest(Heard Heard, MusicPlayer? Player, HelperBot? Playing, ulong? Mentioned);

// One way to go on: what the button and a spoken answer name, and what it does (its reply). About whom, if anyone.
public sealed record VoiceChoice(string Label, Func<Task<string>> RunAsync, ulong? About = null);

// What an action will do: done already (the reply), or a question whose choices confirm it or say which.
public sealed record VoicePlan(string Reply, IReadOnlyList<VoiceChoice>? Choices = null)
{
    public static VoicePlan Done(string reply) => new(reply);

    public static VoicePlan Ask(string question, IReadOnlyList<VoiceChoice> choices) => new(question, choices);
}

// Something members can ask for in their own words: what it is, its arguments (a JSON schema's properties),
// and how it's planned from them.
public sealed record VoiceAction(
    string Name,
    string Description,
    JsonObject Properties,
    string[] Required,
    Func<VoiceRequest, JsonElement, Task<VoicePlan>> PlanAsync);

// A feature's voice actions.
public interface IVoiceActions
{
    IEnumerable<VoiceAction> Actions { get; }
}

public static class Schema
{
    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject OneOf(string description, params string[] values) => new() { ["enum"] = new JsonArray([.. values.Select(v => JsonValue.Create(v))]), ["description"] = description };

    public static string? String(JsonElement args, string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public static int? Int(JsonElement args, string name) => args.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
}
