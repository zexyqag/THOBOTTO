using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Kaomoji;

// /kaomoji: a face found by what it shows, posted as yours. Also in DMs, group DMs and servers without the bot, for
// those who added it.
public sealed class KaomojiCommand(KaomojiShelf shelf) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("kaomoji", "Post a text face like ¯\\_(ツ)_/¯, found by what it shows",
        IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
        Contexts = [InteractionContextType.Guild, InteractionContextType.BotDMChannel, InteractionContextType.DMChannel])]
    public async Task<InteractionMessageProperties> PostAsync(
        [SlashCommandParameter(Description = "What it shows: shrug, table flip, bear, happy…", MaxLength = 100, AutocompleteProviderType = typeof(KaomojiAutocomplete))] string face,
        [SlashCommandParameter(Description = "Words to go before it", MaxLength = 1800)] string? text = null)
    {
        if (await shelf.ForAsync(Context.Interaction.GuildId, Context.User.Id) is not { } faces)
            return Replies.Ephemeral(ModuleRegistry.Off(KaomojiShelf.ModuleId));
        if (KaomojiLibrary.Pick(faces, face) is not { } found)
            return Replies.Ephemeral($"No kaomoji for “{face}”: try what it shows, like shrug, bear or table flip.");
        var escaped = KaomojiLibrary.Escaped(found.Text);
        return new()
        {
            Content = string.IsNullOrWhiteSpace(text) ? escaped : $"{text.Trim()} {escaped}",
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}

public sealed class KaomojiAutocomplete(KaomojiShelf shelf) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        if (await shelf.ForAsync(context.Interaction.GuildId, context.User.Id) is not { } faces)
            return [];
        return KaomojiLibrary.Search(faces, option.Value ?? "").Take(25)
            .Select(f => new ApplicationCommandOptionChoiceProperties(Fit($"{f.Text}  {f.Name}"), f.Key));
    }

    private static string Fit(string label) => label.Length <= 100 ? label : label[..(char.IsHighSurrogate(label[98]) ? 98 : 99)] + "…";
}
