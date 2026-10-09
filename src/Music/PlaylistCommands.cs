using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Lastfm;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Music;

[SlashCommand("playlist", "Saved playlists: save what plays, play one again", Contexts = [InteractionContextType.Guild])]
public sealed class PlaylistCommands(MusicService music, PlaylistBook book, VoicePresence presence, ModuleState modules, AccessControl access, BlendMaker blends)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("save", "Save what plays in your channel (the current track and the queue)")]
    public async Task<InteractionMessageProperties> SaveAsync(
        [SlashCommandParameter(Description = "Name (an existing one of yours is replaced)", MaxLength = PlaylistBook.MaxNameLength, AutocompleteProviderType = typeof(PlaylistAutocomplete))] string name)
    {
        if ((await ModuleOffAsync() ?? MusicCommands.NotInVoice(presence, Context, out _)) is { } refusal)
            return refusal;
        MusicCommands.NotInVoice(presence, Context, out var voiceChannelId);
        if (music.PlayerIn(GuildId, voiceChannelId) is not { } player)
            return Replies.Ephemeral("Nothing is playing in your channel.");

        var tracks = player.Queue.Prepend(player.Current).OfType<Track>().ToList();
        var mayReplaceAny = await access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.ManageMusic);
        return new() { Content = await book.SaveAsync(GuildId, name, Context.User.Id, mayReplaceAny, tracks), AllowedMentions = AllowedMentionsProperties.None };
    }

    [SubSlashCommand("load", "Play a saved playlist in your voice channel")]
    public async Task LoadAsync(
        [SlashCommandParameter(Description = "Playlist", AutocompleteProviderType = typeof(PlaylistAutocomplete))] string name)
    {
        if ((await ModuleOffAsync() ?? MusicCommands.NotInVoice(presence, Context, out _)) is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        MusicCommands.NotInVoice(presence, Context, out var voiceChannelId);
        if (await book.FindAsync(GuildId, name) is not { } found)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("No playlist by that name; `/playlist list` shows them.")));
            return;
        }

        // Joining voice takes a few seconds.
        await RespondAsync(InteractionCallback.DeferredMessage());
        var tracks = found.Tracks.Select(t => t with { RequestedBy = Context.User.Id }).ToList();
        var reply = await music.QueueAsync(GuildId, voiceChannelId, Context.Channel.Id, tracks, found.Playlist.Name);
        await ModifyResponseAsync(m =>
        {
            m.Content = reply;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    [SubSlashCommand("blend", "A mix of the Last.fm taste of everyone in your voice channel who linked it")]
    public async Task BlendAsync(
        [SlashCommandParameter(Description = "How many songs", MinValue = 5, MaxValue = BlendMaker.MaxSize)] int size = BlendMaker.DefaultSize)
    {
        if ((await ModuleOffAsync() ?? MusicCommands.NotInVoice(presence, Context, out _)) is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        MusicCommands.NotInVoice(presence, Context, out var voiceChannelId);

        // Asking Last.fm and finding every song takes a little while.
        await RespondAsync(InteractionCallback.DeferredMessage());
        var (tracks, name, problem) = await blends.BuildAsync(GuildId, voiceChannelId, Context.User.Id, size);
        var reply = problem ?? await music.QueueAsync(GuildId, voiceChannelId, Context.Channel.Id, tracks, name);
        await ModifyResponseAsync(m =>
        {
            m.Content = reply;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    [SubSlashCommand("list", "The server's saved playlists")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var playlists = await book.ListAsync(GuildId);
        if (playlists.Count == 0)
            return Replies.Ephemeral("No saved playlists yet. `/playlist save` saves what plays in your channel.");
        var lines = playlists.Take(50).Select(p => $"**{p.Name}** · {p.TrackCount} tracks · by <@{p.CreatorId}>");
        return new()
        {
            Content = string.Join('\n', lines) + (playlists.Count > 50 ? $"\n…and {playlists.Count - 50} more" : ""),
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }

    [SubSlashCommand("delete", "Delete a saved playlist (yours, or any with music.manage)")]
    public async Task<InteractionMessageProperties> DeleteAsync(
        [SlashCommandParameter(Description = "Playlist", AutocompleteProviderType = typeof(PlaylistAutocomplete))] string name)
    {
        var mayDeleteAny = await access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.ManageMusic);
        return Replies.Ephemeral(await book.DeleteAsync(GuildId, name, Context.User.Id, mayDeleteAny));
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, MusicService.ModuleId) ? null : Replies.Ephemeral($"The `{MusicService.ModuleId}` module is off.");
}

public sealed class PlaylistAutocomplete(PlaylistBook book) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var playlists = await book.ListAsync(context.Interaction.GuildId!.Value);
        return playlists
            .Where(p => p.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new ApplicationCommandOptionChoiceProperties($"{p.Name} ({p.TrackCount} tracks)", p.Name));
    }
}
