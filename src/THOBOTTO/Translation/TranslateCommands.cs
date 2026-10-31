using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Translation;

public sealed class TranslateCommands(Translator translator, ModuleState modules) : GuildCommandModule
{
    public const string ModuleId = "translate";

    // Reading: just for whoever asks, into their own language.
    [MessageCommand("Translate", Contexts = [InteractionContextType.Guild])]
    public async Task TranslateMessageAsync(RestMessage message)
    {
        // A bot's card has its words in the embed.
        var text = string.IsNullOrWhiteSpace(message.Content)
            ? string.Join("\n\n", message.Embeds.SelectMany(e => new[] { e.Title, e.Description }).Where(t => !string.IsNullOrWhiteSpace(t)))
            : message.Content;
        var refused = await RefusedAsync() ?? (text.Length == 0 ? "There's no text in that message to translate." : null);
        if (refused is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refused)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (done, problem) = await translator.TranslateAsync(text, id => Name(id) ?? (message.MentionedUsers.FirstOrDefault(u => u.Id == id) is { } user ? user.GlobalName ?? user.Username : null),
            (languages, _) => Translator.ForReading(languages, Context.Interaction.UserLocale));
        // Without the links' previews: the message itself has them.
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions, m.Flags) = (done is null ? problem : Shown(done, null), AllowedMentionsProperties.None, MessageFlags.SuppressEmbeds));
    }

    // Saying: posted for everyone, as the member's own words. Deferred privately, so a refusal stays private; the
    // translation goes to the channel as a message and the placeholder is removed.
    [SlashCommand("translate", "Say something in another language: the translation is posted for everyone", Contexts = [InteractionContextType.Guild])]
    public async Task TranslateTextAsync(
        [SlashCommandParameter(Description = "What to say", MaxLength = 1500)] string text,
        [SlashCommandParameter(Description = "Into which language (if not given: English, or from English your own)", AutocompleteProviderType = typeof(LanguageAutocomplete))] string? to = null)
    {
        if (await RefusedAsync() is { } refused)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refused)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (done, problem) = await translator.TranslateAsync(text, Name,
            (languages, from) => to is null ? Translator.ForSaying(languages, from, Context.Interaction.UserLocale) : languages.FirstOrDefault(l => l.Code == to));
        if (done is null)
        {
            await ModifyResponseAsync(m => m.Content = problem);
            return;
        }
        try
        {
            await Context.Channel.SendMessageAsync(new() { Content = Shown(done, Context.User.Id), AllowedMentions = AllowedMentionsProperties.None });
        }
        catch (RestException ex)
        {
            await ModifyResponseAsync(m => m.Content = $"I can't post it here ({ex.Message}). In {done.To.Name}:\n{Fit(done.Text, 1800)}");
            return;
        }
        await DeleteResponseAsync();
    }

    private async Task<string?> RefusedAsync()
        => !await modules.IsEnabledAsync(GuildId, ModuleId) ? ModuleRegistry.Off(ModuleId)
            : !translator.On ? Translator.NotSetUp
            : null;

    private string? Name(ulong id) => Guild.Users.TryGetValue(id, out var member) ? member.Nickname ?? member.GlobalName ?? member.Username : null;

    // The translation, then which way it went (and who said it, when it's posted for everyone).
    private static string Shown(Translated done, ulong? by)
    {
        var footer = $"\n-# {(by is { } id ? $"<@{id}> · " : "")}{done.From.Name} → {done.To.Name}";
        return Fit(done.Text, 2000 - footer.Length) + footer;
    }

    private static string Fit(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";
}

public sealed class LanguageAutocomplete(Translator translator) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return (await translator.LanguagesAsync() ?? [])
            .Where(l => l.Name.Contains(input, StringComparison.OrdinalIgnoreCase) || l.Code.Equals(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(l => new ApplicationCommandOptionChoiceProperties(l.Name, l.Code));
    }
}
