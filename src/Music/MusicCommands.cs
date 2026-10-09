using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Music;

[SlashCommand("music", "Music: control what plays, sync channels, helpers", Contexts = [InteractionContextType.Guild])]
public sealed class MusicCommands(MusicService music, VoicePresence presence, LyricsFinder lyrics)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("skip", "Skip the current track (or vote to, when only DJs may)")]
    public async Task<InteractionMessageProperties> SkipAsync()
    {
        if (NotInVoice(out var voiceChannelId) is { } refusal)
            return refusal;
        if (music.PlayerIn(GuildId, voiceChannelId) is not { } player)
            return Replies.Ephemeral("Nothing is playing in your channel.");

        var line = await music.RefusalAsync(GuildId, (GuildUser)Context.User, player, ownTrackAllowed: true) is null
            ? await SkipNowAsync(player)
            : await music.VoteSkipAsync(player, Context.User.Id);
        return new() { Content = line, AllowedMentions = AllowedMentionsProperties.None };
    }

    private async Task<string> SkipNowAsync(MusicPlayer player)
    {
        await player.SkipAsync();
        return await music.LineAsync(player.GuildId, player.Helper, Moments.Skipped);
    }

    [SubSlashCommand("autoplay", "When the queue runs out, keep playing songs like the last one")]
    public Task<InteractionMessageProperties> AutoplayAsync([SlashCommandParameter(Description = "On or off (leave out to switch)")] bool? on = null)
        => ControlAsync(p =>
        {
            p.Autoplay = on ?? !p.Autoplay;
            return Task.FromResult(p.Autoplay ? "♾️ Autoplay on: when the queue runs out, similar songs follow." : "♾️ Autoplay off.");
        });

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
        return await music.LineAsync(p.GuildId, p.Helper, Moments.Stopped);
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

    [SubSlashCommand("lyrics", "The lyrics of what plays in your channel (only you see them)")]
    public async Task LyricsAsync()
    {
        if (NotInVoice(out var voiceChannelId) is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        if (music.PlayerIn(GuildId, voiceChannelId) is not { Current: { } current } player)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Nothing is playing in your channel.")));
            return;
        }

        // Looking them up takes a moment.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (await lyrics.FindAsync(current) is not { } found)
        {
            await ModifyResponseAsync(m => m.Content = $"No lyrics found for **{current.Title}**.");
            return;
        }

        await ModifyResponseAsync(m => m.Embeds = [new()
        {
            Title = $"{found.Title} · {found.Artist}",
            Description = LyricsText(found, player.Position),
            Footer = new() { Text = "Lyrics from LRCLIB" },
        }]);
    }

    // Synced lyrics show where the song is now in bold.
    private static string LyricsText(Lyrics found, long position)
    {
        var text = found.Text;
        if (found.Lines.Count > 0)
        {
            var now = found.Lines.LastOrDefault(l => l.At <= position);
            text = string.Join('\n', found.Lines.Select(l => l == now && l.Text.Length > 0 ? $"**{l.Text}**" : l.Text));
        }
        return text.Length <= 4000 ? text : text[..4000] + "…";
    }

    [SubSlashCommand("helpers", "The helper bots, where they play, and invite links for missing ones")]
    public InteractionMessageProperties Helpers()
    {
        var guildId = Context.Guild!.Id;
        if (music.Helpers.Count == 0)
            return Replies.Ephemeral("The bot has no helper bots yet; its owner adds them in the web panel.");

        var lines = music.Helpers.Select(h => $"{(h.InGuild(guildId) ? "✅" : "➖")} {h.Name}{(h.Players.TryGetValue(guildId, out var p) ? $": playing in <#{p.ChannelOf(h)}>{(p.Helper != h ? $", along with <#{p.VoiceChannelId}>" : "")}" : "")}");
        var missing = music.Helpers.Where(h => !h.InGuild(guildId)).Take(5).ToList();
        return new()
        {
            Content = string.Join('\n', lines) + (missing.Count > 0 ? "\n\nEach helper plays in one channel at a time. Invite the missing ones (someone with Manage Server has to approve):" : ""),
            Components = missing.Count == 0 ? [] : [new ActionRowProperties(missing.Select(h =>
                new LinkButtonProperties(h.InviteUrl(guildId), $"Invite {h.Name}")))],
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

public sealed class PlayCommand(MusicService music, VoicePresence presence, ModuleState modules)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SlashCommand("play", "Play something in your voice channel", Contexts = [InteractionContextType.Guild])]
    public async Task PlayAsync(
        [SlashCommandParameter(Description = "Search words, or a YouTube, Spotify, SoundCloud, Bandcamp, Twitch… link", MaxLength = 300)] string query)
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
        var reply = await music.PlayAsync(GuildId, Context.User.Id, voiceChannelId, Context.Channel.Id, query);
        await ModifyResponseAsync(m =>
        {
            m.Content = reply;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, MusicService.ModuleId) ? null : Replies.Ephemeral($"The `{MusicService.ModuleId}` module is off.");
}

public enum SearchSource
{
    YouTube,
    [SlashCommandChoice(Name = "YouTube Music")] YouTubeMusic,
    SoundCloud,
    Spotify,
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
