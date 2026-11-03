using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Kaomoji;

// /kaomoji: a face found by what it shows (your favourites first), with words before it where kao:shrug is a face too,
// posted as yours: in a server with the bot, under your name and avatar (unless its settings or yours say otherwise);
// elsewhere (DMs, group DMs, servers without the bot, for those who added it) as the bot's reply.
public sealed class KaomojiCommand(KaomojiShelf shelf, KaomojiFavourites favourites, PostingPreferences preferences, AsMember asMember, SettingsStore settings, ModuleState modules)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("kaomoji", "Post a text face like ¯\\_(ツ)_/¯, found by what it shows",
        IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
        Contexts = [InteractionContextType.Guild, InteractionContextType.BotDMChannel, InteractionContextType.DMChannel])]
    public async Task PostAsync(
        [SlashCommandParameter(Description = "What it shows: shrug, table flip, bear, happy…", MaxLength = 100, AutocompleteProviderType = typeof(KaomojiAutocomplete))] string face,
        [SlashCommandParameter(Description = "Words to go before it; kao:shrug, kao:table_flip in them are faces too", MaxLength = 1800)] string? text = null)
    {
        var faces = await shelf.ForAsync(Context.Interaction.GuildId, Context.User.Id);
        var mine = await favourites.TextsAsync(Context.User.Id);
        if (faces is null || KaomojiLibrary.Pick(faces, face, mine) is not { } found)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(faces is null
                ? ModuleRegistry.Off(KaomojiShelf.ModuleId)
                : $"No kaomoji for “{face}”: try what it shows, like shrug, bear or table flip.")));
            return;
        }
        var (words, used) = string.IsNullOrWhiteSpace(text) ? ("", []) : KaomojiLibrary.Fill(text.Trim(), faces, mine);
        var escaped = KaomojiLibrary.Escaped(found.Text);
        var content = words.Length == 0 ? escaped : $"{words} {escaped}";
        if (content.Length > 2000)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("With its faces it's longer than Discord's 2,000 characters.")));
            return;
        }
        var guildId = Context.Interaction.Guild?.Id ?? 0;
        if (!AsMember.Can(Context.Interaction) || !await modules.IsEnabledAsync(guildId, Reposter.ModuleId)
            || !(await settings.GetAsync<KaomojiRules>(guildId, KaomojiShelf.ModuleId)).PostAsMember || !(await preferences.GetAsync(Context.User.Id)).KaomojiAsMe)
        {
            await RespondAsync(InteractionCallback.Message(new() { Content = content, AllowedMentions = AllowedMentionsProperties.None }));
            await UsedAsync();
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var withUsername = (await settings.GetAsync<RepostRules>(guildId, Reposter.ModuleId)).ShowUsername;
        var (_, problem) = await asMember.PostAsync((TextGuildChannel)Context.Interaction.Channel, (GuildInteractionUser)Context.User, withUsername, new() { Content = content });
        if (problem is not null)
            await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = ($"It couldn't be posted as you here ({problem}). To copy: {content}", AllowedMentionsProperties.None));
        else
        {
            await DeleteResponseAsync();
            await UsedAsync();
        }

        async Task UsedAsync()
        {
            foreach (var face in used.Append(found).DistinctBy(f => f.Text))
                await favourites.UsedAsync(Context.User.Id, face.Text);
        }
    }
}

public sealed class KaomojiAutocomplete(KaomojiShelf shelf, KaomojiFavourites favourites) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        if (await shelf.ForAsync(context.Interaction.GuildId, context.User.Id) is not { } faces)
            return [];
        var mine = await favourites.TextsAsync(context.User.Id);
        return KaomojiLibrary.Favoured(KaomojiLibrary.Search(faces, option.Value ?? ""), mine).Take(25)
            .Select(f => new ApplicationCommandOptionChoiceProperties(Fit(mine.Contains(f.Text) ? $"{f.Text}  ★ {f.Name}" : $"{f.Text}  {f.Name}"), f.Key));
    }

    private static string Fit(string label) => label.Length <= 100 ? label : label[..(char.IsHighSurrogate(label[98]) ? 98 : 99)] + "…";
}
