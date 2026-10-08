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
        await p.SkipAsync();
        return await music.LineAsync(p.Helper, Moments.Skipped);
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
        return await music.LineAsync(p.Helper, Moments.Stopped);
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
    // View Channel, Send Messages, Embed Links, Connect, Speak, Change Nickname: what a helper uses.
    private const ulong HelperPermissions = 1024 | 2048 | 16384 | 1048576 | 2097152 | 67108864;

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

[SlashCommand("helper", "The music helpers' characters", Contexts = [InteractionContextType.Guild])]
[RequirePermission(BotPermissions.ManageMusic)]
public sealed class HelperCommands(MusicService music, Personalities personalities) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("personality", "Nickname, colour, avatar")]
    public async Task PersonalityAsync(
        [SlashCommandParameter(Description = "Helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string helper,
        [SlashCommandParameter(Description = "Name in servers; \"none\" for its account name", MaxLength = 32)] string? nickname = null,
        [SlashCommandParameter(Description = "Colour of its messages, e.g. #ff3b7f", MaxLength = 7)] string? colour = null,
        [SlashCommandParameter(Description = "New avatar (changes the account everywhere; Discord limits how often)")] Attachment? avatar = null)
    {
        if (Find(helper) is not { } bot)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("There's no such helper.")));
            return;
        }
        int? rgb = null;
        if (colour is not null)
        {
            if (!int.TryParse(colour.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var parsed) || colour.TrimStart('#').Length != 6)
            {
                await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Colours are hex, like `#ff3b7f`.")));
                return;
            }
            rgb = parsed;
        }

        // Nicknames go to every server and avatars upload; that takes a moment.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (nickname is not null || rgb is not null)
        {
            await personalities.SaveAsync(bot.UserId, p =>
            {
                p.Nickname = nickname is null ? p.Nickname : nickname.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : nickname.Trim();
                p.Color = rgb ?? p.Color;
            });
            if (nickname is not null)
                await music.ApplyNicknameAsync(bot);
        }

        var avatarResult = "";
        if (avatar is not null)
        {
            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(avatar.Url);
                var format = avatar.ContentType switch { "image/png" => ImageFormat.Png, "image/gif" => ImageFormat.Gif, "image/webp" => ImageFormat.Webp, _ => ImageFormat.Jpeg };
                await bot.Gateway.Rest.ModifyCurrentUserAsync(u => u.Avatar = new ImageProperties(format, bytes, false));
                avatarResult = " Avatar changed.";
            }
            catch (RestException ex)
            {
                avatarResult = $" Discord refused the avatar: {ex.Message}";
            }
        }

        var name = await music.DisplayNameAsync(bot);
        await ModifyResponseAsync(m => m.Content = $"Saved {name}.{avatarResult}");
    }

    [SubSlashCommand("phrases", "What a helper says at a moment: list, add, remove, or reset to its built-in lines")]
    public async Task<InteractionMessageProperties> PhrasesAsync(
        [SlashCommandParameter(Description = "Helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string helper,
        [SlashCommandParameter(Description = "When it says it")] Moment moment,
        [SlashCommandParameter(Description = "What to do")] PhraseAction action = PhraseAction.List,
        [SlashCommandParameter(Description = "The phrase to add, or the number to remove; {track} {user} {channel} {helper} get filled in", MaxLength = 300)] string? text = null)
    {
        if (Find(helper) is not { } bot)
            return Replies.Ephemeral("There's no such helper.");

        var key = moment.ToString().ToLowerInvariant();
        var current = (await personalities.PhrasesAsync(bot.UserId, bot.Index, key)).ToList();
        switch (action)
        {
            case PhraseAction.Add when !string.IsNullOrWhiteSpace(text):
                current.Add(text.Trim());
                await personalities.SaveAsync(bot.UserId, p => p.Phrases[key] = current);
                break;
            case PhraseAction.Remove when int.TryParse(text, out var n) && n >= 1 && n <= current.Count:
                current.RemoveAt(n - 1);
                await personalities.SaveAsync(bot.UserId, p => p.Phrases[key] = current);
                current = (await personalities.PhrasesAsync(bot.UserId, bot.Index, key)).ToList();
                break;
            case PhraseAction.Reset:
                await personalities.SaveAsync(bot.UserId, p => p.Phrases.Remove(key));
                current = (await personalities.PhrasesAsync(bot.UserId, bot.Index, key)).ToList();
                break;
            case PhraseAction.Add or PhraseAction.Remove:
                return Replies.Ephemeral(action == PhraseAction.Add ? "Give the phrase in `text`." : "Give the number to remove in `text`.");
        }

        return new()
        {
            Content = $"**{await music.DisplayNameAsync(bot)}**, {key}:\n" + string.Join('\n', current.Select((p, i) => $"{i + 1}. {p}")),
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }

    private HelperBot? Find(string id) => music.Helpers.FirstOrDefault(h => h.UserId.ToString() == id);
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
