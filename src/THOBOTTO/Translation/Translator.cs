using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using THOBOTTO.Integrations;

namespace THOBOTTO.Translation;

public sealed record Language(string Code, string Name);

public sealed record Translated(string Text, Language From, Language To);

// LibreTranslate on the stack (only the languages it loads), or another instance with its key; set on the
// Integrations page.
public sealed class Translator(IntegrationStore store, [FromKeyedServices(nameof(Translator))] HttpClient http, TimeProvider time, ILogger<Translator> logger)
{
    public const string NotSetUp = "Translation isn't set up: the bot's owner sets a translation server on the panel's Integrations page.";
    private const string Unreachable = "The translation server doesn't answer right now.";
    private static readonly TimeSpan LanguagesHeld = TimeSpan.FromMinutes(10);

    private (string Url, DateTimeOffset At, IReadOnlyList<Language> Languages)? _languages;

    public bool On => Url is not null;

    private string? Url => store.Get(IntegrationStore.TranslationUrl)?.TrimEnd('/');

    // What it translates, asked again every ten minutes or at a new address (fresh: now, for the Status page); null
    // when it doesn't answer.
    public async Task<IReadOnlyList<Language>?> LanguagesAsync(bool fresh = false, CancellationToken ct = default)
    {
        if (Url is not { } url)
            return null;
        if (!fresh && _languages is { } known && known.Url == url && time.GetUtcNow() - known.At < LanguagesHeld)
            return known.Languages;
        try
        {
            var languages = await http.GetFromJsonAsync<List<Language>>($"{url}/languages", ct) ?? [];
            _languages = (url, time.GetUtcNow(), languages);
            return languages;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("The translation server's languages couldn't be read: {Message}", ex.Message);
            return null;
        }
    }

    // The text in the language target picks for the one it's in, or why not.
    public async Task<(Translated? Done, string? Problem)> TranslateAsync(string text, Func<ulong, string?> name, Func<IReadOnlyList<Language>, Language, Language?> target, CancellationToken ct = default)
    {
        if (Url is not { } url)
            return (null, NotSetUp);
        var pieces = TranslatableText.Split(text, name);
        if (pieces.Said is not { Count: > 0 } said)
            return (null, "There are no words in that to translate.");
        if (await LanguagesAsync(ct: ct) is not { } languages)
            return (null, Unreachable);
        try
        {
            // LibreTranslate's detector takes Danish for Norwegian, and with Norwegian not loaded finds nothing (English,
            // at no confidence). Its word lists know better, and it checks only text under 20 characters against them.
            var code = await DetectAsync(url, [string.Join('\n', said)], ct) ?? await DetectAsync(url, TranslatableText.Short(said), ct);
            if (languages.FirstOrDefault(l => l.Code == code) is not { } from)
                return (null, $"It can't tell which language that's in; it knows {string.Join(", ", languages.Select(l => l.Name))}.");
            if (target(languages, from) is not { } to)
                return (null, $"It doesn't translate into that; it knows {string.Join(", ", languages.Select(l => l.Name))}.");
            if (to.Code == from.Code)
                return (null, $"That's already in {from.Name}.");
            using var done = await PostAsync(url, "translate", new JsonObject
            {
                ["q"] = new JsonArray([.. said.Select(s => JsonValue.Create(s))]),
                ["source"] = from.Code,
                ["target"] = to.Code,
                ["format"] = "text",
            }, ct);
            var translated = done.RootElement.GetProperty("translatedText").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
            return translated.Count == said.Count ? (new(TranslatableText.Join(pieces, translated), from, to), null) : (null, Unreachable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            logger.LogWarning("Translating failed: {Message}", ex.Message);
            return (null, Unreachable);
        }
    }

    // The language, when it's at all sure.
    private async Task<string?> DetectAsync(string url, IReadOnlyList<string> text, CancellationToken ct)
    {
        using var detected = await PostAsync(url, "detect", new JsonObject { ["q"] = new JsonArray([.. text.Select(t => JsonValue.Create(t))]) }, ct);
        var best = detected.RootElement[0];
        return best.GetProperty("confidence").GetDouble() > 0 ? best.GetProperty("language").GetString() : null;
    }

    private async Task<JsonDocument> PostAsync(string url, string path, JsonObject body, CancellationToken ct)
    {
        if (store.Get(IntegrationStore.TranslationKey) is { } key)
            body["api_key"] = key;
        using var response = await http.PostAsJsonAsync($"{url}/{path}", body, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
    }

    // For reading: the reader's own language, as Discord is set to, else English.
    public static Language? ForReading(IReadOnlyList<Language> languages, string locale)
        => Find(languages, locale) ?? Find(languages, "en");

    // For saying something: into English, or from English into the speaker's own language, else the first other.
    public static Language? ForSaying(IReadOnlyList<Language> languages, Language from, string locale)
        => from.Code != "en" ? Find(languages, "en") ?? languages.FirstOrDefault(l => l.Code != from.Code)
            : Find(languages, locale) is { Code: not "en" } own ? own
            : languages.FirstOrDefault(l => l.Code != "en");

    // Discord's locales are "da" or "en-US"; LibreTranslate's codes mostly the first part.
    private static Language? Find(IReadOnlyList<Language> languages, string locale)
        => languages.FirstOrDefault(l => l.Code.Equals(locale, StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault(l => l.Code.Equals(locale.Split('-')[0], StringComparison.OrdinalIgnoreCase));
}
