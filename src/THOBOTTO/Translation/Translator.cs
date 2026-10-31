using System.Text.Json;

using THOBOTTO.Integrations;

namespace THOBOTTO.Translation;

public sealed record Language(string Code, string Name);

public sealed record Translated(string Text, Language From, Language To);

// Translations through the engine picked on the Integrations page: LibreTranslate at its address (the default, as it
// came first), or DeepL with its key.
public sealed class Translator(IntegrationStore store, [FromKeyedServices(nameof(Translator))] HttpClient http, TimeProvider time, ILogger<Translator> logger)
{
    public const string NotSetUp = "Translation isn't set up: the bot's owner picks a translation service on the panel's Integrations page.";
    public const string LibreTranslateEngine = "libretranslate", DeepLEngine = "deepl";
    private const string Unreachable = "The translation service doesn't answer right now.";
    private static readonly TimeSpan LanguagesHeld = TimeSpan.FromMinutes(10);
    // What a text is in before it's been asked.
    private static readonly Language Unknown = new("", "");

    private (string Settings, ITranslationEngine Engine)? _engine;
    private (string Settings, DateTimeOffset At, IReadOnlyList<Language> Languages)? _languages;

    public bool On => Engine() is not null;

    private string Settings => $"{store.Get(IntegrationStore.TranslationEngine)}|{store.Get(IntegrationStore.DeepLKey)}|{store.Get(IntegrationStore.TranslationUrl)}|{store.Get(IntegrationStore.TranslationKey)}";

    public ITranslationEngine? Engine()
    {
        var settings = Settings;
        if (_engine is { } known && known.Settings == settings)
            return known.Engine;
        ITranslationEngine? made = store.Get(IntegrationStore.TranslationEngine) == DeepLEngine
            ? store.Get(IntegrationStore.DeepLKey) is { } deepl ? new DeepL(deepl) : null
            : store.Get(IntegrationStore.TranslationUrl)?.TrimEnd('/') is { } url ? new LibreTranslate(http, url, store.Get(IntegrationStore.TranslationKey)) : null;
        _engine = made is null ? null : (settings, made);
        return made;
    }

    // What it translates, asked again every ten minutes or when the settings change (fresh: now, for the Status
    // page); null when it doesn't answer.
    public async Task<IReadOnlyList<Language>?> LanguagesAsync(bool fresh = false, CancellationToken ct = default)
    {
        if (Engine() is not { } engine)
            return null;
        var settings = Settings;
        if (!fresh && _languages is { } known && known.Settings == settings && time.GetUtcNow() - known.At < LanguagesHeld)
            return known.Languages;
        try
        {
            var languages = await engine.LanguagesAsync(ct);
            _languages = (settings, time.GetUtcNow(), languages);
            return languages;
        }
        catch (Exception ex) when (Failed(ex))
        {
            logger.LogWarning("{Engine}'s languages couldn't be read: {Message}", engine.Name, ex.Message);
            return null;
        }
    }

    // The text in the language target picks for the one it's in, or why not.
    public async Task<(Translated? Done, string? Problem)> TranslateAsync(string text, Func<ulong, string?> name, Func<IReadOnlyList<Language>, Language, Language?> target, CancellationToken ct = default)
    {
        if (Engine() is not { } engine)
            return (null, NotSetUp);
        var pieces = TranslatableText.Split(text, name);
        if (pieces.Said.Count == 0)
            return (null, "There are no words in that to translate.");
        if (await LanguagesAsync(ct: ct) is not { } languages)
            return (null, Unreachable);
        try
        {
            return await TranslateAsync(engine, languages, pieces, target, ct);
        }
        catch (Exception ex) when (Failed(ex))
        {
            logger.LogWarning("Translating with {Engine} failed: {Message}", engine.Name, ex.Message);
            return (null, ex is global::DeepL.QuotaExceededException ? "DeepL's characters for this month are used up." : Unreachable);
        }
    }

    // Engines say what a text is in as they translate it, so the first ask is for the language the rule picks knowing
    // nothing, and a second only when knowing changes that.
    public static async Task<(Translated? Done, string? Problem)> TranslateAsync(ITranslationEngine engine, IReadOnlyList<Language> languages, TranslatableText.Pieces pieces,
        Func<IReadOnlyList<Language>, Language, Language?> target, CancellationToken ct)
    {
        var known = string.Join(", ", languages.Select(l => l.Name));
        if (target(languages, Unknown) is not { } guess)
            return (null, $"It doesn't translate into that; it knows {known}.");
        var (code, texts) = await engine.TranslateAsync(pieces.Said, null, guess.Code, ct);
        if (code is null || Find(languages, code) is not { } from)
            return (null, $"It can't tell which language that's in; it knows {known}.");
        if (target(languages, from) is not { } to)
            return (null, $"It doesn't translate into that; it knows {known}.");
        if (to.Code == from.Code)
            return (null, $"That's already in {from.Name}.");
        if (to.Code != guess.Code)
            (_, texts) = await engine.TranslateAsync(pieces.Said, from.Code, to.Code, ct);
        return texts.Count == pieces.Said.Count ? (new(TranslatableText.Join(pieces, texts), from, to), null) : (null, Unreachable);
    }

    // How many of DeepL's characters this month are used; null when it isn't DeepL or doesn't say.
    public async Task<(long Used, long Limit)?> UsageAsync()
    {
        try
        {
            return Engine() is DeepL deepl ? await deepl.UsageAsync() : null;
        }
        catch (Exception ex) when (Failed(ex))
        {
            return null;
        }
    }

    private static bool Failed(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or global::DeepL.DeepLException;

    // For reading: the reader's own language, as Discord is set to, else English.
    public static Language? ForReading(IReadOnlyList<Language> languages, string locale)
        => Find(languages, locale) ?? Find(languages, "en");

    // For saying something: into English, or from English into the speaker's own language, else the first other.
    public static Language? ForSaying(IReadOnlyList<Language> languages, Language from, string locale)
        => from.Code != "en" ? Find(languages, "en") ?? languages.FirstOrDefault(l => l.Code != from.Code)
            : Find(languages, locale) is { Code: not "en" } own ? own
            : languages.FirstOrDefault(l => l.Code != "en");

    // Discord's locales are "da" or "en-US"; the engines' codes mostly the first part.
    private static Language? Find(IReadOnlyList<Language> languages, string locale)
        => languages.FirstOrDefault(l => l.Code.Equals(locale, StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault(l => l.Code.Equals(locale.Split('-')[0], StringComparison.OrdinalIgnoreCase));
}
