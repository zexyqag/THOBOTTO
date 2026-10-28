using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Discord;

namespace THOBOTTO.Sounds;

[SlashCommand("sound", "The server's soundboard: play, propose, your join sound", Contexts = [InteractionContextType.Guild])]
public sealed class SoundCommands(SoundBoard board) : GuildCommandModule
{
    [SubSlashCommand("play", "Play a sound in your voice channel")]
    public async Task PlayAsync(
        [SlashCommandParameter(Description = "Which sound", AutocompleteProviderType = typeof(SoundNames))] string name)
    {
        // A helper may have to hop in first.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var reply = await board.PlayAsync(GuildId, Context.User.Id, name);
        await ModifyResponseAsync(m => m.Content = reply);
    }

    [SubSlashCommand("propose", "Put a sound up for a vote (MP3, OGG or WAV)")]
    public Task ProposeAsync(
        [SlashCommandParameter(Description = "Its name", MaxLength = 32)] string name,
        [SlashCommandParameter(Description = "The sound file")] Attachment file)
        => AddAsync(name, file, direct: false);

    [SubSlashCommand("add", "Add a sound straight to the library (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public Task AddDirectAsync(
        [SlashCommandParameter(Description = "Its name", MaxLength = 32)] string name,
        [SlashCommandParameter(Description = "The sound file")] Attachment file)
        => AddAsync(name, file, direct: true);

    private async Task AddAsync(string name, Attachment file, bool direct)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var reply = await board.AddAsync(GuildId, name, Context.User.Id, file, direct);
        await ModifyResponseAsync(m => m.Content = reply);
    }

    [SubSlashCommand("remove", "Take a sound out of the library (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public async Task<InteractionMessageProperties> RemoveAsync(
        [SlashCommandParameter(Description = "Which sound", AutocompleteProviderType = typeof(SoundNames))] string name)
        => Replies.Ephemeral(await board.RemoveAsync(GuildId, name));

    [SubSlashCommand("discord", "Pin a sound on Discord's own soundboard, or take it off (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public async Task OnDiscordAsync(
        [SlashCommandParameter(Description = "Which sound", AutocompleteProviderType = typeof(SoundNames))] string name,
        [SlashCommandParameter(Description = "On Discord's soundboard or not")] bool on)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var reply = await board.OnDiscordAsync(GuildId, name, on);
        await ModifyResponseAsync(m => m.Content = reply);
    }

    [SubSlashCommand("rename", "Give a sound another name, on Discord's soundboard too (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public async Task RenameAsync(
        [SlashCommandParameter(Description = "Which sound", AutocompleteProviderType = typeof(SoundNames))] string name,
        [SlashCommandParameter(Name = "new-name", Description = "Its new name", MaxLength = 32)] string newName)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var reply = await board.RenameAsync(GuildId, name, newName);
        await ModifyResponseAsync(m => m.Content = reply);
    }

    [SubSlashCommand("import", "Bring the library and Discord's soundboard in step now (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public async Task ImportAsync()
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var reply = (await board.ReconcileAsync(GuildId, Context.User.Id)).Describe();
        await ModifyResponseAsync(m => m.Content = reply);
    }

    [SubSlashCommand("list", "The sounds in the library")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var library = await board.LibraryAsync(GuildId);
        return Replies.Ephemeral(library.Count == 0 ? "The library is empty: `/sound propose` a sound."
            : string.Join(" · ", library.Select(s => $"`{s.Name}`{(s.DiscordId is null ? "" : s.Pinned ? " 📌" : " 🎛️")}")) + "\n-# 🎛️ also on Discord's soundboard, 📌 pinned there");
    }

    [SubSlashCommand("join", "Your join sounds: add one (again removes it), none without a name; several play at random")]
    public async Task<InteractionMessageProperties> JoinAsync(
        [SlashCommandParameter(Description = "Which sound", AutocompleteProviderType = typeof(SoundNames))] string? name = null)
        => Replies.Ephemeral(name is null ? await board.ClearJoinSoundsAsync(GuildId, Context.User.Id) : await board.ToggleJoinSoundAsync(GuildId, Context.User.Id, name));
}

public sealed class SoundNames(SoundBoard board) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        if (context.Interaction.GuildId is not { } guildId)
            return [];
        var typed = option.Value ?? "";
        return (await board.LibraryAsync(guildId))
            .Where(s => s.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(s => new ApplicationCommandOptionChoiceProperties(s.Name, s.Name));
    }
}

public sealed class SoundVoteButtons(SoundBoard board) : GuildButtonModule
{
    [ComponentInteraction("soundvote")]
    public async Task<InteractionMessageProperties> VoteAsync(long id, int up) => Replies.Ephemeral(await board.VoteAsync(id, Context.User.Id, up == 1));
}
