using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace THOBOTTO.Translation;

// A translation service: the texts in the language to, and the language they were in (told when from is null: null
// when it can't tell).
public interface ITranslationEngine
{
    string Name { get; }

    Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct);

    Task<(string? From, IReadOnlyList<string> Texts)> TranslateAsync(IReadOnlyList<string> texts, string? from, string to, CancellationToken ct);
}

// LibreTranslate on the stack (only the languages it loads), or another instance with its key.
public sealed class LibreTranslate(HttpClient http, string url, string? key) : ITranslationEngine
{
    public string Name => "LibreTranslate";

    public async Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct) => await http.GetFromJsonAsync<List<Language>>($"{url}/languages", ct) ?? [];

    public async Task<(string? From, IReadOnlyList<string> Texts)> TranslateAsync(IReadOnlyList<string> texts, string? from, string to, CancellationToken ct)
    {
        // Its detector takes Danish for Norwegian, and with Norwegian not loaded finds nothing (English, at no
        // confidence). Its word lists know better, and it checks only text under 20 characters against them.
        from ??= await DetectAsync([string.Join('\n', texts)], ct) ?? await DetectAsync(TranslatableText.Short(texts), ct);
        if (from is null || from == to)
            return (from, texts);
        using var done = await PostAsync("translate", new JsonObject
        {
            ["q"] = new JsonArray([.. texts.Select(s => JsonValue.Create(s))]),
            ["source"] = from,
            ["target"] = to,
            ["format"] = "text",
        }, ct);
        return (from, [.. done.RootElement.GetProperty("translatedText").EnumerateArray().Select(t => t.GetString() ?? "")]);
    }

    // The language, when it's at all sure.
    private async Task<string?> DetectAsync(IReadOnlyList<string> text, CancellationToken ct)
    {
        using var detected = await PostAsync("detect", new JsonObject { ["q"] = new JsonArray([.. text.Select(t => JsonValue.Create(t))]) }, ct);
        var best = detected.RootElement[0];
        return best.GetProperty("confidence").GetDouble() > 0 ? best.GetProperty("language").GetString() : null;
    }

    private async Task<JsonDocument> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        if (key is not null)
            body["api_key"] = key;
        using var response = await http.PostAsJsonAsync($"{url}/{path}", body, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
    }
}

// DeepL through its own library (a free key translates 500,000 characters a month). It has no way to ask a text's
// language alone: it says so with each translation.
public sealed class DeepL(string key) : ITranslationEngine, IDisposable
{
    private readonly global::DeepL.DeepLClient _client = new(key);

    public string Name => "DeepL";

    public async Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct)
        => [.. (await _client.GetSourceLanguagesAsync(ct)).Select(l => new Language(l.Code.ToLowerInvariant(), l.Name))];

    public async Task<(string? From, IReadOnlyList<string> Texts)> TranslateAsync(IReadOnlyList<string> texts, string? from, string to, CancellationToken ct)
    {
        // It wants English and Portuguese as one country's.
        var target = to switch { "en" => "en-GB", "pt" => "pt-PT", _ => to };
        var results = await _client.TranslateTextAsync(texts, from, target, null, ct);
        return (results.FirstOrDefault()?.DetectedSourceLanguageCode.ToLowerInvariant(), [.. results.Select(r => r.Text)]);
    }

    // This month's characters, and how many it may.
    public async Task<(long Used, long Limit)?> UsageAsync(CancellationToken ct = default)
        => (await _client.GetUsageAsync(ct)).Character is { } characters ? (characters.Count, characters.Limit) : null;

    public void Dispose() => _client.Dispose();
}
