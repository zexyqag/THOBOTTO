using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Characters;

// /character: your characters, in every server that has them on (also from DMs, for those who added the bot).
[SlashCommand("character", "Characters your messages can go under, by the brackets you write them in",
    IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
    Contexts = [InteractionContextType.Guild, InteractionContextType.BotDMChannel, InteractionContextType.DMChannel])]
public sealed class CharacterCommands(CharacterBook book, [FromKeyedServices(nameof(CharacterBook))] HttpClient http) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("add", "A new character: write g:hello (with brackets g:text) and it's posted under its name")]
    public async Task<InteractionMessageProperties> AddAsync(
        [SlashCommandParameter(Description = "Its name", MaxLength = CharacterBook.NameLength)] string name,
        [SlashCommandParameter(Description = "What goes around the word text, like g:text or [text]", MaxLength = 2 * CharacterBook.BracketLength + 4)] string brackets,
        [SlashCommandParameter(Description = "Its picture: PNG, JPEG, GIF or WebP, at most 1 MB")] Attachment? picture = null)
        => Replies.Ephemeral(await SaveAsync(null, name, brackets, picture) ?? $"**{name.Trim()}** is yours: write {brackets.Trim().Replace("text", "hello", StringComparison.OrdinalIgnoreCase)} where characters are on.");

    [SubSlashCommand("edit", "Change a character's name, brackets or picture")]
    public async Task<InteractionMessageProperties> EditAsync(
        [SlashCommandParameter(Description = "Which", AutocompleteProviderType = typeof(CharacterAutocomplete))] string character,
        [SlashCommandParameter(Description = "A new name", MaxLength = CharacterBook.NameLength)] string? name = null,
        [SlashCommandParameter(Description = "New brackets, like g:text", MaxLength = 2 * CharacterBook.BracketLength + 4)] string? brackets = null,
        [SlashCommandParameter(Description = "A new picture")] Attachment? picture = null)
        => await FindAsync(character) is not { } found
            ? Replies.Ephemeral("No character of yours by that name.")
            : Replies.Ephemeral(await SaveAsync(found.Id, name, brackets, picture) ?? "Saved.");

    [SubSlashCommand("remove", "Remove one of your characters")]
    public async Task<InteractionMessageProperties> RemoveAsync(
        [SlashCommandParameter(Description = "Which", AutocompleteProviderType = typeof(CharacterAutocomplete))] string character)
        => Replies.Ephemeral(await FindAsync(character) is { } found && await book.RemoveAsync(Context.User.Id, found.Id) ? $"**{found.Name}** is gone." : "No character of yours by that name.");

    [SubSlashCommand("list", "Your characters and their brackets")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var mine = await book.OfAsync(Context.User.Id);
        return Replies.Ephemeral(mine.Count == 0
            ? "You have no characters yet: `/character add`, or on the panel's You page."
            : string.Join('\n', mine.Select(c => $"**{c.Name}** · `{c.Brackets}`{(c.AvatarKey.Length > 0 ? "" : " · no picture")}")));
    }

    private async Task<Character?> FindAsync(string given)
        => (await book.OfAsync(Context.User.Id)).FirstOrDefault(c => c.Id.ToString() == given || c.Name.Equals(given.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<string?> SaveAsync(long? id, string? name, string? brackets, Attachment? picture)
    {
        if (picture is not null && picture.Size > CharacterBook.AvatarBytes)
            return $"A picture is at most {CharacterBook.AvatarBytes / 1024 / 1024} MB.";
        var avatar = picture is null ? ((byte[], string)?)null : (await http.GetByteArrayAsync(picture.Url), picture.ContentType ?? "");
        return await book.SaveAsync(Context.User.Id, id, name, brackets, avatar);
    }
}

public sealed class CharacterAutocomplete(CharacterBook book) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
        => (await book.OfAsync(context.User.Id))
            .Where(c => c.Name.Contains(option.Value ?? "", StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(c => new ApplicationCommandOptionChoiceProperties($"{c.Name} · {c.Brackets}", c.Id.ToString()));
}

public static class CharactersStartup
{
    public static IServiceCollection AddCharacters(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(CharacterBook), c => c.Timeout = TimeSpan.FromSeconds(30)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<CharacterBook>()
            .AddHostedService(s => s.GetRequiredService<CharacterBook>())
            .AddSingleton<IRewrite, CharacterVoice>()
            .AddSingleton<IPostingSource, CharacterPostings>();
    }

    // A character's picture, for Discord to show on its posts: no sign-in (Discord fetches it); the key is the secret.
    public static WebApplication MapCharacters(this WebApplication app)
    {
        app.MapGet("/characters/{id:long}/{key}", async (long id, string key, CharacterBook book)
            => await book.AvatarAsync(id, key) is { Avatar: { } bytes } found ? Results.File(bytes, found.AvatarType) : Results.NotFound());
        return app;
    }
}
