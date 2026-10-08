using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Music;

public sealed partial class MusicCommands(MusicService music, VoicePresence presence, ModuleState modules, SettingsStore settings, AccessControl access)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SlashCommand("play", "Play something in your voice channel", Contexts = [InteractionContextType.Guild])]
    public async Task PlayAsync(
        [SlashCommandParameter(Description = "Search words, or a YouTube, SoundCloud, Bandcamp, Twitch… link", MaxLength = 300)] string query)
    {
        var refusal = await ModuleOffAsync() ?? NotInVoice(out _);
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        NotInVoice(out var voiceChannelId);

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

    [SlashCommand("skip", "Skip the current track", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> SkipAsync() => ControlAsync(async p =>
    {
        var title = p.Current?.Title;
        await p.SkipAsync();
        return $"⏭️ Skipped {title}.";
    }, ownTrackAllowed: true);

    [SlashCommand("pause", "Pause the music", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> PauseAsync() => ControlAsync(async p =>
    {
        await p.SetPausedAsync(true);
        return "⏸️ Paused.";
    });

    [SlashCommand("resume", "Resume the music", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> ResumeAsync() => ControlAsync(async p =>
    {
        await p.SetPausedAsync(false);
        return "▶️ Resumed.";
    });

    [SlashCommand("stop", "Stop, clear the queue, and send the helper away", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> StopAsync() => ControlAsync(async p =>
    {
        await p.StopAsync();
        await music.DisconnectAsync(p);
        return "⏹️ Stopped.";
    });

    [SlashCommand("volume", "Set the volume", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> VolumeAsync([SlashCommandParameter(Description = "Percent", MinValue = 0, MaxValue = 200)] int percent)
        => ControlAsync(async p =>
        {
            await p.SetVolumeAsync(percent);
            return $"🔊 Volume {percent}%.";
        });

    [SlashCommand("loop", "Loop the track, the queue, or nothing", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> LoopAsync([SlashCommandParameter(Description = "What to loop")] LoopMode mode)
        => ControlAsync(p =>
        {
            p.Loop = mode;
            return Task.FromResult(mode == LoopMode.Off ? "🔁 Not looping." : $"🔁 Looping the {mode.ToString().ToLowerInvariant()}.");
        });

    [SlashCommand("shuffle", "Shuffle the queue", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> ShuffleAsync() => ControlAsync(async p =>
    {
        await p.ShuffleAsync();
        return "🔀 Shuffled.";
    });

    [SlashCommand("queue", "What's playing and what's next", Contexts = [InteractionContextType.Guild])]
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

    private async Task<InteractionMessageProperties> ControlAsync(Func<MusicPlayer, Task<string>> action, bool ownTrackAllowed = false)
    {
        if (NotInVoice(out var voiceChannelId) is { } refusal)
            return refusal;
        if (music.PlayerIn(GuildId, voiceChannelId) is not { } player)
            return Replies.Ephemeral("Nothing is playing in your channel.");

        var rules = await settings.GetAsync<MusicRules>(GuildId, MusicService.ModuleId);
        var own = ownTrackAllowed && player.Current?.RequestedBy == Context.User.Id;
        if (rules.DjOnly && !own && !await access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.MusicDj))
            return Replies.Ephemeral($"That needs `{BotPermissions.MusicDj}` here.");

        return new() { Content = await action(player), AllowedMentions = AllowedMentionsProperties.None };
    }

    private InteractionMessageProperties? NotInVoice(out ulong voiceChannelId)
    {
        voiceChannelId = presence.Snapshot(GuildId).TryGetValue(Context.User.Id, out var where) ? where.ChannelId : 0;
        return voiceChannelId == 0 ? Replies.Ephemeral("Join a voice channel first.") : null;
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, MusicService.ModuleId) ? null : Replies.Ephemeral($"The `{MusicService.ModuleId}` module is off.");

    [GeneratedRegex(@"^[a-z]{2,5}search:")]
    private static partial Regex SourcePrefix();
}

[SlashCommand("music", "Music helpers and settings", Contexts = [InteractionContextType.Guild])]
public sealed class MusicAdminCommands(MusicService music, SettingsStore settings) : ApplicationCommandModule<ApplicationCommandContext>
{
    // View Channel, Connect, Speak: all a helper needs.
    private const ulong HelperPermissions = 1024 | 1048576 | 2097152;

    [SubSlashCommand("helpers", "The music helper bots, with invite links for missing ones")]
    public InteractionMessageProperties Helpers()
    {
        var guildId = Context.Guild!.Id;
        if (music.Helpers.Count == 0)
            return Replies.Ephemeral("No music helpers are configured for the bot.");

        var lines = music.Helpers.Select(h => $"{(h.InGuild(guildId) ? "✅" : "➖")} {h.Name}{(h.Players.TryGetValue(guildId, out var p) ? $": playing in <#{p.VoiceChannelId}>" : "")}");
        var missing = music.Helpers.Where(h => !h.InGuild(guildId)).Take(5).ToList();
        return new()
        {
            Content = string.Join('\n', lines) + (missing.Count > 0 ? "\n\nEach helper plays in one channel at a time. Invite the missing ones (someone with Manage Server has to approve):" : ""),
            Components = missing.Count == 0 ? [] : [new ActionRowProperties(missing.Select(h =>
                new LinkButtonProperties($"https://discord.com/oauth2/authorize?client_id={h.UserId}&scope=bot&permissions={HelperPermissions}&guild_id={guildId}&disable_guild_select=true", $"Invite {h.Name}")))],
            Flags = MessageFlags.Ephemeral,
        };
    }

    [SubSlashCommand("settings", "Idle time, queue size, search source, DJ rule (needs music.manage)")]
    [RequirePermission(BotPermissions.ManageMusic)]
    public async Task<InteractionMessageProperties> SettingsAsync(
        [SlashCommandParameter(Name = "idle-minutes", Description = "Leave after this long with nobody listening or nothing playing", MinValue = 1, MaxValue = 120)] int? idleMinutes = null,
        [SlashCommandParameter(Name = "max-queue", Description = "Most tracks in a queue", MinValue = 1, MaxValue = 5000)] int? maxQueue = null,
        [SlashCommandParameter(Name = "search", Description = "Where plain words are searched")] SearchSource? search = null,
        [SlashCommandParameter(Name = "dj-only", Description = "Skip, stop, pause and volume need music.dj (your own track you can always skip)")] bool? djOnly = null)
    {
        var guildId = Context.Guild!.Id;
        var before = await settings.GetAsync<MusicRules>(guildId, MusicService.ModuleId);
        var after = before with
        {
            IdleMinutes = idleMinutes ?? before.IdleMinutes,
            MaxQueue = maxQueue ?? before.MaxQueue,
            DefaultSearch = search switch { SearchSource.YouTube => "ytsearch", SearchSource.SoundCloud => "scsearch", _ => before.DefaultSearch },
            DjOnly = djOnly ?? before.DjOnly,
        };
        var changed = after != before;
        if (changed)
            await settings.SetAsync(guildId, MusicService.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Leave after {after.IdleMinutes} min idle · queue up to {after.MaxQueue} · search {(after.DefaultSearch == "scsearch" ? "SoundCloud" : "YouTube")} · controls: {(after.DjOnly ? $"`{BotPermissions.MusicDj}` only" : "anyone")}
            """);
    }
}

public enum SearchSource
{
    YouTube,
    SoundCloud,
}

public sealed class MusicButtons(MusicService music, SettingsStore settings, AccessControl access) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("music")]
    public async Task<InteractionMessageProperties> ControlAsync(string action, ulong helperId)
    {
        var guildId = Context.Guild!.Id;
        var player = music.Helpers.FirstOrDefault(h => h.UserId == helperId)?.Players.GetValueOrDefault(guildId);
        if (player is null)
            return Replies.Ephemeral("That player has stopped.");

        var rules = await settings.GetAsync<MusicRules>(guildId, MusicService.ModuleId);
        var own = action == "skip" && player.Current?.RequestedBy == Context.User.Id;
        if (rules.DjOnly && !own && !await access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.MusicDj))
            return Replies.Ephemeral($"That needs `{BotPermissions.MusicDj}` here.");

        switch (action)
        {
            case "pause":
                await player.SetPausedAsync(!player.Paused);
                return Replies.Ephemeral(player.Paused ? "⏸️ Paused." : "▶️ Resumed.");
            case "skip":
                await player.SkipAsync();
                return Replies.Ephemeral("⏭️ Skipped.");
            default:
                await player.StopAsync();
                await music.DisconnectAsync(player);
                return Replies.Ephemeral("⏹️ Stopped.");
        }
    }
}
