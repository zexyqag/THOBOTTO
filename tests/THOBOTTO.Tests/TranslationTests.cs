using THOBOTTO.Translation;

namespace THOBOTTO.Tests;

public class TranslationTests
{
    private static readonly Language English = new("en", "English");
    private static readonly Language Danish = new("da", "Danish");
    private static readonly Language German = new("de", "German");

    private static string? Named(ulong id) => id == 1 ? "Marty" : null;

    // Says a text is Danish when it has "hej", English otherwise (or nothing, when told it can't tell), and translates
    // by naming the language in front.
    private sealed class Engine(bool tells = true) : ITranslationEngine
    {
        public List<(string? From, string To)> Asked { get; } = [];

        public string Name => "Test";

        public Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Language>>([English, Danish]);

        public Task<(string? From, IReadOnlyList<string> Texts)> TranslateAsync(IReadOnlyList<string> texts, string? from, string to, CancellationToken ct)
        {
            Asked.Add((from, to));
            var said = from ?? (tells ? (texts[0].Contains("hej", StringComparison.OrdinalIgnoreCase) ? "da" : "en") : null);
            return Task.FromResult<(string?, IReadOnlyList<string>)>((said, [.. texts.Select(t => $"{to}:{t}")]));
        }
    }

    private static Task<(Translated? Done, string? Problem)> TranslateAsync(Engine engine, string text, Func<IReadOnlyList<Language>, Language, Language?> target)
        => Translator.TranslateAsync(engine, [English, Danish], TranslatableText.Split(text, Named), target, CancellationToken.None);

    [Fact]
    public async Task Reading_takes_one_ask_when_the_first_guess_is_right()
    {
        var engine = new Engine();
        var (done, _) = await TranslateAsync(engine, "Hej alle", (l, _) => Translator.ForReading(l, "en-US"));
        Assert.Equal(("en:Hej alle", "da", "en"), (done!.Text, done.From.Code, done.To.Code));
        Assert.Equal([(null, "en")], engine.Asked);
    }

    [Fact]
    public async Task Saying_something_in_english_asks_again_for_the_speakers_own_language()
    {
        var engine = new Engine();
        var (done, _) = await TranslateAsync(engine, "See you tonight", (l, from) => Translator.ForSaying(l, from, "da"));
        Assert.Equal(("da:See you tonight", "en", "da"), (done!.Text, done.From.Code, done.To.Code));
        Assert.Equal([(null, "en"), ("en", "da")], engine.Asked);
    }

    [Fact]
    public async Task What_is_already_in_the_language_or_cant_be_told_or_isnt_known_says_so()
    {
        Assert.Equal("That's already in English.", (await TranslateAsync(new Engine(), "See you", (l, _) => Translator.ForReading(l, "en-GB"))).Problem);
        Assert.StartsWith("It can't tell which language", (await TranslateAsync(new Engine(tells: false), "Hmm", (l, _) => Translator.ForReading(l, "en-GB"))).Problem);
        Assert.StartsWith("It doesn't translate into that", (await TranslateAsync(new Engine(), "Hej", (l, _) => l.FirstOrDefault(x => x.Code == "de"))).Problem);
    }

    [Fact]
    public void Only_words_are_sent_and_a_member_mentioned_is_said_by_name()
    {
        var pieces = TranslatableText.Split("Hej <@1>, kommer du i aften? 🎉 <:pog:42>\n> se https://store.steampowered.com/app/892970/ og `/event plan` <@2>", Named);
        Assert.Equal(["Hej @Marty, kommer du i aften?", "se", "og"], pieces.Said);
        Assert.Equal("Marty", pieces.Names[1]);
        Assert.Contains(pieces.Parts, p => p is { Text: "<@2>", Said: false });
    }

    [Fact]
    public void Spoilers_and_links_in_text_are_kept_around_their_words_and_bold_and_italics_dropped()
    {
        var pieces = TranslatableText.Split("Husk at købe **meget** øl og *chips*\n* Har du set ||spoileren||? [læs her](https://a.example/x)", Named);
        Assert.Equal(["Husk at købe meget øl og chips", "Har du set", "spoileren", "læs her"], pieces.Said);
        var back = TranslatableText.Join(pieces, ["Remember to buy lots of beer and chips", "Have you seen", "the spoiler", "read here"]);
        Assert.Equal("Remember to buy lots of beer and chips\n* Have you seen ||the spoiler||? [read here](https://a.example/x)", back);
    }

    [Fact]
    public void Short_pieces_are_under_twenty_characters_at_spaces()
        => Assert.Equal(["Jeg kommer lidt", "senere, min bus er", "forsinket"], TranslatableText.Short(["Jeg kommer lidt senere, min bus er forsinket"]));

    [Fact]
    public void Translations_go_back_with_their_spaces_mentions_and_times_put_right()
    {
        var pieces = TranslatableText.Split("  Hej <@1>, vi ses kl. 20:00 <:pog:42> ", Named);
        var back = TranslatableText.Join(pieces, ["Hi @ Marty, see you at 8: 00"]);
        Assert.Equal("  Hi <@1>, see you at 8:00 <:pog:42> ", back);
    }

    [Fact]
    public void Nothing_but_emojis_and_links_has_no_words()
        => Assert.Empty(TranslatableText.Split("😂😂 https://a.example/ <:pog:42> !!", Named).Said);

    [Theory]
    [InlineData("da", "da")]
    [InlineData("en-US", "en")]
    [InlineData("de", "en")]
    public void Reading_is_into_the_readers_language_else_english(string locale, string expected)
        => Assert.Equal(expected, Translator.ForReading([English, Danish], locale)?.Code);

    [Theory]
    [InlineData("da", "en-US", "en")]
    [InlineData("en", "da", "da")]
    [InlineData("en", "en-GB", "da")]
    [InlineData("de", "da", "en")]
    public void Saying_is_into_english_or_from_english_into_the_speakers_language(string from, string locale, string expected)
    {
        Language[] languages = [English, Danish, German];
        Assert.Equal(expected, Translator.ForSaying(languages, languages.Single(l => l.Code == from), locale)?.Code);
    }
}
