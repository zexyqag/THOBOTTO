using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Kaomoji;

// /kaomoji: a face found by what it shows, posted as yours: in a server with the bot, under your name and avatar
// (unless its settings say otherwise); elsewhere (DMs, group DMs, servers without the bot, for those who added it) as
// the bot's reply.
public sealed class KaomojiCommand(KaomojiShelf shelf, AsMember asMember, SettingsStore settings) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("kaomoji", "Post a text face like ¯\\_(ツ)_/¯, found by what it shows",
        IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
        Contexts = [InteractionContextType.Guild, InteractionContextType.BotDMChannel, InteractionContextType.DMChannel])]
    public async Task PostAsync(
        [SlashCommandParameter(Description = "What it shows: shrug, table flip, bear, happy…", MaxLength = 100, AutocompleteProviderType = typeof(KaomojiAutocomplete))] string face,
        [SlashCommandParameter(Description = "Words to go before it", MaxLength = 1800)] string? text = null)
    {
        var faces = await shelf.ForAsync(Context.Interaction.GuildId, Context.User.Id);
        if (faces is null || KaomojiLibrary.Pick(faces, face) is not { } found)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(faces is null
                ? ModuleRegistry.Off(KaomojiShelf.ModuleId)
                : $"No kaomoji for “{face}”: try what it shows, like shrug, bear or table flip.")));
            return;
        }
        var escaped = KaomojiLibrary.Escaped(found.Text);
        var content = string.IsNullOrWhiteSpace(text) ? escaped : $"{text.Trim()} {escaped}";
        if (!AsMember.Can(Context.Interaction) || !(await settings.GetAsync<KaomojiRules>(Context.Interaction.Guild!.Id, KaomojiShelf.ModuleId)).PostAsMember)
        {
            await RespondAsync(InteractionCallback.Message(new() { Content = content, AllowedMentions = AllowedMentionsProperties.None }));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (await asMember.PostAsync(Context.Interaction, content) is { } problem)
            await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = ($"It couldn't be posted as you here ({problem}). To copy: {content}", AllowedMentionsProperties.None));
        else
            await DeleteResponseAsync();
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
