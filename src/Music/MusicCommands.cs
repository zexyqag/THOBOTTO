using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Music;

[SlashCommand("music", "Music: control what plays, sync channels, helpers", Contexts = [InteractionContextType.Guild])]
public sealed class MusicCommands(MusicService music, VoicePresence presence)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("skip", "Skip the current track")]
    public Task<InteractionMessageProperties> SkipAsync() => ControlAsync(async p =>
    {
        await p.SkipAsync();
        return await music.LineAsync(p.Helper, Moments.Skipped);
    }, ownTrackAllowed: true);

    [SubSlashCommand("pause", "Pause the music")]
    public Task<InteractionMessageProperties> PauseAsync() => ControlAsync(async p =>
    {
        await p.SetPausedAsync(true);
        return "⏸️ Paused.";
    });

    [SubSlashCommand("resume", "Resume the music")]
    public Task<InteractionMessageProperties> ResumeAsync() => ControlAsync(async p =>
    {
        await p.SetPausedAsync(false);
        return "▶️ Resumed.";
    });

    [SubSlashCommand("stop", "Stop, clear the queue, and send the helper away")]
    public Task<InteractionMessageProperties> StopAsync() => ControlAsync(async p =>
    {
        await p.StopAsync();
        await music.DisconnectAsync(p);
        return await music.LineAsync(p.Helper, Moments.Stopped);
    });

    [SubSlashCommand("volume", "Set the volume")]
    public Task<InteractionMessageProperties> VolumeAsync([SlashCommandParameter(Description = "Percent", MinValue = 0, MaxValue = 200)] int percent)
        => ControlAsync(async p =>
        {
            await p.SetVolumeAsync(percent);
            return $"🔊 Volume {percent}%.";
        });

    [SubSlashCommand("loop", "Loop the track, the queue, or nothing")]
    public Task<InteractionMessageProperties> LoopAsync([SlashCommandParameter(Description = "What to loop")] LoopMode mode)
        => ControlAsync(p =>
        {
            p.Loop = mode;
            return Task.FromResult(mode == LoopMode.Off ? "🔁 Not looping." : $"🔁 Looping the {mode.ToString().ToLowerInvariant()}.");
        });

    [SubSlashCommand("shuffle", "Shuffle the queue")]
    public Task<InteractionMessageProperties> ShuffleAsync() => ControlAsync(async p =>
    {
        await p.ShuffleAsync();
        return "🔀 Shuffled.";
    });

    [SubSlashCommand("queue", "What's playing and what's next")]
    public async Task<InteractionMessageProperties> QueueAsync()
    {
        if (NotInVoice(out var voiceChannelId) is { } refusal)
            return refusal;
        if (music.PlayerIn(GuildId, voiceChannelId) is not { Current: { } current } player)
            return Replies.Ephemeral("Nothing is playing in your channel.");

        var next = player.Queue.Take(15).Select((t, i) => $"{i + 1}. {t.Markdown} · {t.Length}").ToList();
        var more = player.Queue.Count > 15 ? $"\n…and {player.Queue.Count - 15} more" : "";
        return new()
        {
            Content = $"🎵 {current.Markdown} · {current.Length}{(player.Paused ? " (paused)" : "")}\n\n{(next.Count == 0 ? "Nothing queued." : string.Join('\n', next))}{more}",
            Flags = MessageFlags.Ephemeral | MessageFlags.SuppressEmbeds,
        };
    }

    // View Channel, Send Messages, Embed Links, Connect, Speak, Change Nickname: what a helper uses.
    private const ulong HelperPermissions = 1024 | 2048 | 16384 | 1048576 | 2097152 | 67108864;

    [SubSlashCommand("helpers", "The music helper bots, with invite links for missing ones")]
    public InteractionMessageProperties Helpers()
    {
        var guildId = Context.Guild!.Id;
        if (music.Helpers.Count == 0)
            return Replies.Ephemeral("No music helpers are configured for the bot.");

        var lines = music.Helpers.Select(h => $"{(h.InGuild(guildId) ? "✅" : "➖")} {h.Name}{(h.Players.TryGetValue(guildId, out var p) ? $": playing in <#{p.ChannelOf(h)}>{(p.Helper != h ? $", along with <#{p.VoiceChannelId}>" : "")}" : "")}");
        var missing = music.Helpers.Where(h => !h.InGuild(guildId)).Take(5).ToList();
        return new()
        {
            Content = string.Join('\n', lines) + (missing.Count > 0 ? "\n\nEach helper plays in one channel at a time. Invite the missing ones (someone with Manage Server has to approve):" : ""),
            Components = missing.Count == 0 ? [] : [new ActionRowProperties(missing.Select(h =>
                new LinkButtonProperties($"https://discord.com/oauth2/authorize?client_id={h.UserId}&scope=bot&permissions={HelperPermissions}&guild_id={guildId}&disable_guild_select=true", $"Invite {h.Name}")))],
            Flags = MessageFlags.Ephemeral,
        };
    }

    [SubSlashCommand("sync", "Play your channel's music in another voice channel too, in step")]
    public async Task SyncAsync(
        [SlashCommandParameter(Description = "Voice channel to play along", AllowedChannelTypes = [ChannelType.VoiceGuildChannel, ChannelType.StageGuildChannel])] Channel channel)
    {
        if (await ControllableAsync() is not { } player)
            return;

        // Joining voice takes a few seconds.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var problem = await music.SyncAsync(player, channel.Id);
        await ModifyResponseAsync(m => m.Content = problem ?? $"🔗 <#{channel.Id}> plays along now.");
    }

    [SubSlashCommand("unsync", "Stop a channel playing along (yours, or the one given)")]
    public async Task UnsyncAsync(
        [SlashCommandParameter(Description = "Voice channel playing along", AllowedChannelTypes = [ChannelType.VoiceGuildChannel, ChannelType.StageGuildChannel])] Channel? channel = null)
    {
        if (await ControllableAsync() is not { } player)
            return;

        // Without a channel: from the queue's own channel every one playing along goes, else your own.
        var mirrors = player.Mirrors.Where(m => channel is null ? player.VoiceChannelId == VoiceChannelId || m.VoiceChannelId == VoiceChannelId : m.VoiceChannelId == channel.Id).ToList();
        if (mirrors.Count == 0)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(channel?.Id == player.VoiceChannelId
                ? "The queue plays from that channel; `/music stop` there ends it."
                : "No channel plays along there.")));
            return;
        }

        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        foreach (var mirror in mirrors)
            await music.UnsyncAsync(player, mirror);
        await ModifyResponseAsync(m => m.Content = $"⛓️‍💥 {string.Join(", ", mirrors.Select(x => $"<#{x.VoiceChannelId}>"))} stopped playing along.");
    }

    private ulong VoiceChannelId => NotInVoice(out var channelId) is null ? channelId : 0;

    // The player in the user's channel when they may control it; otherwise answers why not.
    private async Task<MusicPlayer?> ControllableAsync()
    {
        string problem;
        if (VoiceChannelId == 0)
            problem = "Join the voice channel with the music first.";
        else if (music.PlayerIn(Context.Guild!.Id, VoiceChannelId) is not { } player)
            problem = "Nothing is playing in your channel.";
        else if (await music.RefusalAsync(Context.Guild.Id, (GuildUser)Context.User, player) is { } refusal)
            problem = refusal;
        else
            return player;

        await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(problem)));
        return null;
    }

    private async Task<InteractionMessageProperties> ControlAsync(Func<MusicPlayer, Task<string>> action, bool ownTrackAllowed = false)
    {
        if (NotInVoice(out var voiceChannelId) is { } refusal)
            return refusal;
        if (music.PlayerIn(GuildId, voiceChannelId) is not { } player)
            return Replies.Ephemeral("Nothing is playing in your channel.");

        if (await music.RefusalAsync(GuildId, (GuildUser)Context.User, player, ownTrackAllowed) is { } denied)
            return Replies.Ephemeral(denied);

        return new() { Content = await action(player), AllowedMentions = AllowedMentionsProperties.None };
    }

    private InteractionMessageProperties? NotInVoice(out ulong voiceChannelId) => NotInVoice(presence, Context, out voiceChannelId);

    // Shared with /play.
    internal static InteractionMessageProperties? NotInVoice(VoicePresence presence, ApplicationCommandContext context, out ulong voiceChannelId)
    {
        voiceChannelId = presence.Snapshot(context.Guild!.Id).TryGetValue(context.User.Id, out var where) ? where.ChannelId : 0;
        return voiceChannelId == 0 ? Replies.Ephemeral("Join a voice channel first.") : null;
    }

}

public sealed partial class PlayCommand(MusicService music, VoicePresence presence, ModuleState modules, SettingsStore settings)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SlashCommand("play", "Play something in your voice channel", Contexts = [InteractionContextType.Guild])]
    public async Task PlayAsync(
        [SlashCommandParameter(Description = "Search words, or a YouTube, SoundCloud, Bandcamp, Twitch… link", MaxLength = 300)] string query)
    {
        var refusal = await ModuleOffAsync() ?? MusicCommands.NotInVoice(presence, Context, out _);
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        MusicCommands.NotInVoice(presence, Context, out var voiceChannelId);

        // Loading tracks and joining voice take a few seconds.
        await RespondAsync(InteractionCallback.DeferredMessage());
        var rules = await settings.GetAsync<MusicRules>(GuildId, MusicService.ModuleId);
        // Links and explicit sources ("scsearch:", "ytsearch:", …) go as typed; plain words to the default search.
        var text = query.Trim();
        var asTyped = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" || SourcePrefix().IsMatch(text);
        var identifier = asTyped ? text : $"{rules.DefaultSearch}:{text}";

        LoadResult loaded;
        try
        {
            loaded = await LavalinkConnection.LoadAsync(music.Lavalink, identifier, Context.User.Id);
        }
        catch (HttpRequestException)
        {
            await ModifyResponseAsync(m => m.Content = "The music server isn't reachable right now.");
            return;
        }

        if (loaded.Error is { } error || loaded.Tracks.Count == 0)
        {
            await ModifyResponseAsync(m => m.Content = loaded.Error is null ? "Nothing found." : $"Couldn't load that: {loaded.Error}");
            return;
        }

        var (player, problem) = await music.PlayerForAsync(GuildId, voiceChannelId, Context.Channel.Id);
        if (player is null)
        {
            await ModifyResponseAsync(m => m.Content = problem);
            return;
        }

        // A search plays its best match; a playlist goes in whole, up to the queue limit.
        var room = Math.Max(0, rules.MaxQueue - player.Queue.Count);
        var tracks = (loaded.Playlist is null ? loaded.Tracks.Take(1) : loaded.Tracks.Take(room)).ToList();
        if (tracks.Count == 0)
        {
            await ModifyResponseAsync(m => m.Content = $"The queue is full ({rules.MaxQueue}).");
            return;
        }

        var position = await player.EnqueueAsync(tracks);
        var what = loaded.Playlist is { } name ? $"**{tracks.Count}** tracks from **{name}**" : tracks[0].Markdown;
        await ModifyResponseAsync(m =>
        {
            m.Content = position == 0 ? $"▶️ {what}" : $"➕ Queued {what} (#{position})";
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, MusicService.ModuleId) ? null : Replies.Ephemeral($"The `{MusicService.ModuleId}` module is off.");

    [GeneratedRegex(@"^[a-z]{2,5}search:")]
    private static partial Regex SourcePrefix();
}

public enum SearchSource
{
    YouTube,
    SoundCloud,
}

public sealed class MusicButtons(MusicService music) : ComponentInteractionModule<ButtonInteractionContext>
{
    // Buttons on now-playing messages the main bot posted for a helper; a helper's own reach it directly.
    [ComponentInteraction("music")]
    public async Task<InteractionMessageProperties> ControlAsync(string action, ulong helperId)
        => new()
        {
            Content = await music.ButtonAsync(Context.Guild!.Id, (GuildUser)Context.User, action, helperId),
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
}

public enum Moment
{
    Joined,
    Playing,
    Skipped,
    Stopped,
    Finished,
    Lonely,
}

public enum PhraseAction
{
    List,
    Add,
    Remove,
    Reset,
}

public sealed class HelperAutocomplete(MusicService music) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var choices = new List<ApplicationCommandOptionChoiceProperties>();
        foreach (var helper in music.Helpers)
            choices.Add(new($"{await music.DisplayNameAsync(helper)} ({helper.Name})", helper.UserId.ToString()));
        return choices;
    }
}
