using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Expressions;
using THOBOTTO.Happenings;
using THOBOTTO.Helpers;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Sounds;

// The server's own soundboard: as many sounds as it likes, proposed and voted on like emojis (managers add
// directly), played by a helper in the voice channel over the music, and join sounds for anyone, no Nitro needed.
// Where no helper is in the channel, a free one can hop in to play it. Creators earn royalties on plays.
public sealed partial class SoundBoard(
    IDbContextFactory<BotDbContext> dbFactory,
    RestClient rest,
    GatewayClient gateway,
    DiscordApi discord,
    ModuleState modules,
    SettingsStore settings,
    AccessControl access,
    PointsEngine points,
    Notifier notifier,
    HappeningForum forum,
    VoicePresence presence,
    HelperHops hops,
    VoiceMouths mouths,
    HelperSpeech speech,
    [FromKeyedServices(nameof(SoundBoard))] HttpClient http,
    TimeProvider time,
    ILogger<SoundBoard> logger) : BackgroundService, IMemberLeft
{
    public const string ModuleId = "sounds";
    public const int MaxBytes = 2 * 1024 * 1024;
    private const int MostKept = 100;

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Decoded sounds, ready to play.
    private readonly ConcurrentDictionary<long, (short[] Stereo, TimeSpan Length)> _decoded = new();
    // (server, member) → when they last played one, or their join sound last played.
    private readonly ConcurrentDictionary<(ulong, ulong), DateTimeOffset> _played = new();
    private readonly ConcurrentDictionary<(ulong, ulong), DateTimeOffset> _joined = new();
    // Helpers hopping in right now.
    // (server, creator) → (day, royalties paid that day).
    private readonly ConcurrentDictionary<(ulong, ulong), (DateOnly Day, double Paid)> _royalties = new();

    public ValueTask<SoundRules> RulesAsync(ulong guildId) => settings.GetAsync<SoundRules>(guildId, ModuleId);

    public async Task<IReadOnlyList<Sound>> LibraryAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId && s.State == SoundStates.Library).OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<Sound?> FileAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sounds.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId && s.Id == id && s.State != SoundStates.Removed);
    }

    public async Task<IReadOnlyList<Sound>> VotingAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId && s.State == SoundStates.Voting).OrderBy(s => s.VoteEndsAt).ToListAsync();
    }

    public async Task<Sound?> FindAsync(ulong guildId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return Pick(await db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId && s.State == SoundStates.Library).ToListAsync(), name);
    }

    // The sound called that, or the one named most like it.
    public static Sound? Pick(IReadOnlyList<Sound> library, string name)
        => library.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? library.Select(s => (Sound: s, Score: Likeness.Of(name, s.Name))).Where(s => s.Score >= 0.7).OrderByDescending(s => s.Score).Select(s => s.Sound).FirstOrDefault();

    // A proposal (voted on in the emoji and sticker voting channel), or straight into the library for managers.
    public async Task<string> AddAsync(ulong guildId, string name, ulong userId, Attachment file, bool direct, ulong channelId)
    {
        if (!SoundDecoder.Known(file.FileName))
            return "Sounds must be MP3, OGG or WAV.";
        if (file.Size > MaxBytes)
            return $"That file is {file.Size / 1024} KB; sounds can be at most {MaxBytes / 1024 / 1024} MB.";
        return await AddFileAsync(guildId, name, userId, await http.GetByteArrayAsync(file.Url), file.FileName, direct, channelId);
    }

    // From the panel's upload, or a Discord attachment.
    // From where it was proposed: a channel, or none (the panel).
    public async Task<string> AddFileAsync(ulong guildId, string name, ulong userId, byte[] bytes, string fileName, bool direct, ulong? channelId)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return ModuleRegistry.Off(ModuleId);
        name = name.Trim();
        if (!ValidName().IsMatch(name))
            return "Sound names are 2 to 32 letters, digits, spaces, - or _.";
        if (!SoundDecoder.Known(fileName))
            return "Sounds must be MP3, OGG or WAV.";
        if (bytes.Length > MaxBytes)
            return $"That file is {bytes.Length / 1024} KB; sounds can be at most {MaxBytes / 1024 / 1024} MB.";
        if (SoundDecoder.Decode(bytes, fileName) is not { } decoded)
            return "I couldn't read that as audio.";
        var rules = await RulesAsync(guildId);
        if (decoded.Length.TotalSeconds > rules.MaxSeconds)
            return $"That's {decoded.Length.TotalSeconds:0.#} s; sounds here can be at most {rules.MaxSeconds} s.";
        return await OpenAsync(guildId, name, userId, bytes, fileName, (int)decoded.Length.TotalMilliseconds, direct, null, channelId);
    }

    private async Task<string> OpenAsync(ulong guildId, string name, ulong userId, byte[] bytes, string fileName, int ms, bool direct, ulong? discordId, ulong? channelId = null)
    {
        var votes = await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId);
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var lowered = name.ToLowerInvariant();
            if (await db.Sounds.AnyAsync(s => s.GuildId == guildId && s.Name.ToLower() == lowered && (s.State == SoundStates.Voting || s.State == SoundStates.Library)))
                return $"There's already a sound called `{name}`.";
            var price = !direct && await points.ChargesAsync(guildId) ? votes.ProposeCost : 0;
            if (price > 0 && !await points.TrySpendAsync(guildId, userId, price, $"sound proposal {name}"))
                return $"Proposing costs {points.Rules(guildId).Format(price)} (refunded if it's accepted).";

            var now = time.GetUtcNow();
            var sound = new Sound
            {
                GuildId = guildId,
                Name = name,
                CreatorId = userId,
                ProposerId = userId,
                Audio = bytes,
                FileName = fileName,
                Milliseconds = ms,
                State = direct ? SoundStates.Library : SoundStates.Voting,
                Paid = price,
                ProposedAt = now,
                VoteEndsAt = now + TimeSpan.FromDays(votes.VoteDays),
                DecidedAt = direct ? now : null,
                DiscordId = discordId,
            };
            db.Sounds.Add(sound);
            await db.SaveChangesAsync();
            if (direct)
                return $"🔊 `{name}` is in the library.";

            if (channelId is { } here)
            {
                var message = await rest.SendMessageAsync(here, new()
                {
                    Embeds = [Embed(sound, 0, 0, votes)],
                    Attachments = [new AttachmentProperties(fileName, new MemoryStream(bytes))],
                    Components = [Buttons(sound, votes)],
                    AllowedMentions = AllowedMentionsProperties.None,
                });
                (sound.VoteChannelId, sound.VoteMessageId) = (message.ChannelId, message.Id);
                await db.SaveChangesAsync();
            }
            var inForum = await forum.ShowAsync(guildId, HappeningKinds.Sound, sound.Id, HappeningStates.Open, Card(sound, 0, 0, votes));
            return Proposals.UpForVote("sound", channelId is not null, inForum);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> VoteAsync(long id, ulong userId, bool up)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.Sounds.FindAsync(id) is not { State: SoundStates.Voting } sound)
                return Proposals.VotingEnded;
            if (userId == sound.ProposerId)
                return Proposals.OwnProposal;
            var votes = await settings.GetAsync<ExpressionRules>(sound.GuildId, ExpressionShelf.ModuleId);
            if (!up && !votes.DownVotes)
                return Proposals.DownVotesOff;
            if (await db.SoundVotes.FindAsync(id, userId) is { } vote)
                vote.Up = up;
            else
                db.SoundVotes.Add(new() { SoundId = id, UserId = userId, Up = up });
            await db.SaveChangesAsync();
            var (ups, downs) = await CountAsync(db, id);
            if (Proposals.Accepted(ups, downs, votes))
                await DecideAsync(db, sound, accepted: true, votes);
            else
                await RenderAsync(sound, ups, downs, votes);
            await points.InteractAsync(sound.GuildId, userId, InteractionKind.Vote, $"sound {id}");
            return Proposals.VoteReply(up);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RemoveAsync(ulong guildId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await FindAsync(guildId, name) is not { } found || await db.Sounds.FindAsync(found.Id) is not { } sound)
            return $"There's no sound called `{name}`.";
        sound.State = SoundStates.Removed;
        sound.RemovedAt = time.GetUtcNow();
        // A linked copy on Discord goes too while the boards are kept in step; otherwise the link stays on the
        // record, so an import doesn't bring the sound back.
        var note = "";
        if (sound.DiscordId is { } discordId)
        {
            if (!(await RulesAsync(guildId)).DiscordSync)
                note = " It stays on Discord's soundboard.";
            else if (await discord.RemoveSoundAsync(guildId, discordId))
                sound.DiscordId = null;
            else
                note = " It stays on Discord's soundboard: I need the Manage Expressions permission to take it off.";
        }
        await db.JoinSounds.Where(j => j.SoundId == sound.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        _decoded.TryRemove(sound.Id, out _);
        return $"Removed `{sound.Name}`.{note}";
    }

    // Plays a sound in the member's voice channel; the answer to give them.
    public async Task<string> PlayAsync(ulong guildId, ulong userId, string name)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return ModuleRegistry.Off(ModuleId);
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var where))
            return "Join a voice channel first.";
        var rules = await RulesAsync(guildId);
        if (rules.PlayNeedsPermission && !(gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var member) && await access.CanAsync(guild, member, BotPermissions.SoundsPlay)))
            return $"Playing sounds needs `{BotPermissions.SoundsPlay}` here.";
        if (_played.TryGetValue((guildId, userId), out var last) && time.GetUtcNow() - last < TimeSpan.FromSeconds(rules.CooldownSeconds))
            return $"Wait a moment between sounds ({rules.CooldownSeconds} s).";
        if (rules.ChannelPerMinute > 0 && !ChannelHasRoom(guildId, where.ChannelId, rules.ChannelPerMinute))
            return $"That's {rules.ChannelPerMinute} sounds within a minute here; wait a little.";
        if (await FindAsync(guildId, name) is not { } sound)
            return $"There's no sound called `{name}`. `/sound list` shows them.";
        _played[(guildId, userId)] = time.GetUtcNow();
        if (!await PlayInAsync(guildId, where.ChannelId, sound, rules))
            return "No helper is free to play it there.";
        await CountPlayAsync(sound, userId);
        return $"🔊 {sound.Name}";
    }

    // (server, voice channel) → when sounds played there this last minute.
    private readonly ConcurrentDictionary<(ulong Guild, ulong Channel), List<DateTimeOffset>> _channelPlays = new();

    // Whether the channel's minute still has room for a sound, counting this one if so.
    private bool ChannelHasRoom(ulong guildId, ulong channelId, int perMinute)
    {
        var now = time.GetUtcNow();
        var recent = _channelPlays.GetOrAdd((guildId, channelId), _ => []);
        lock (recent)
        {
            recent.RemoveAll(at => now - at > TimeSpan.FromMinutes(1));
            if (recent.Count >= perMinute)
                return false;
            recent.Add(now);
            return true;
        }
    }

    // A moment for a hub to move them to their own channel: the sound plays where they are by then.
    private static readonly TimeSpan JoinSettle = TimeSpan.FromSeconds(1.5);

    // Arriving in a channel plays their join sound, without holding up the rest.
    private Task ArrivedAsync(VoiceState state, ulong? before)
    {
        if (state.ChannelId is { } channelId && channelId != before && state.User?.IsBot != true)
            _ = JoinSoundAsync(state.GuildId, state.UserId, channelId);
        return Task.CompletedTask;
    }

    private async Task JoinSoundAsync(ulong guildId, ulong userId, ulong channelId)
    {
        try
        {
            await JoinedAsync(guildId, userId, channelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A join sound in {ChannelId} of {GuildId} failed: {Message}", channelId, guildId, ex.Message);
        }
    }

    // Someone (not a bot) arrived in a voice channel.
    private async Task JoinedAsync(ulong guildId, ulong userId, ulong channelId)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return;
        await Task.Delay(JoinSettle, time);
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var now))
            return;
        channelId = now.ChannelId;
        // Landing in the AFK channel isn't an arrival.
        if (gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.AfkChannelId == channelId)
            return;
        var rules = await RulesAsync(guildId);
        if (!rules.JoinSounds || _joined.TryGetValue((guildId, userId), out var last) && time.GetUtcNow() - last < TimeSpan.FromMinutes(rules.JoinCooldownMinutes))
            return;
        var sounds = await JoinSoundsOfAsync(guildId, userId);
        if (sounds.Count == 0)
            return;
        _joined[(guildId, userId)] = time.GetUtcNow();
        await PlayInAsync(guildId, channelId, sounds[Random.Shared.Next(sounds.Count)], rules);
    }

    // A sound added to the member's join sounds, or taken off them when it's there already.
    public async Task<string> ToggleJoinSoundAsync(ulong guildId, ulong userId, string name)
    {
        if (await FindAsync(guildId, name) is not { } sound)
            return $"There's no sound called `{name}`.";
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.JoinSounds.FindAsync(guildId, userId, sound.Id) is { } current)
        {
            db.JoinSounds.Remove(current);
            await db.SaveChangesAsync();
            return $"`{sound.Name}` is no longer one of your join sounds.";
        }
        db.JoinSounds.Add(new() { GuildId = guildId, UserId = userId, SoundId = sound.Id });
        await db.SaveChangesAsync();
        var count = await db.JoinSounds.CountAsync(j => j.GuildId == guildId && j.UserId == userId);
        return count == 1 ? $"Your join sound is `{sound.Name}`." : $"`{sound.Name}` is one of your {count} join sounds; one plays at random.";
    }

    public async Task<string> ClearJoinSoundsAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.JoinSounds.Where(j => j.GuildId == guildId && j.UserId == userId).ExecuteDeleteAsync();
        return "No join sound for you any more.";
    }

    // The member's join sounds that are still in the library, by name.
    public async Task<IReadOnlyList<Sound>> JoinSoundsOfAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.JoinSounds.Where(j => j.GuildId == guildId && j.UserId == userId).Select(j => j.SoundId).ToListAsync();
        return await db.Sounds.AsNoTracking().Where(s => ids.Contains(s.Id) && s.State == SoundStates.Library).OrderBy(s => s.Name).ToListAsync();
    }

    public async Task MemberLeftAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.JoinSounds.Where(j => j.GuildId == guildId && j.UserId == userId).ExecuteDeleteAsync();
    }

    // Through a helper there, else (if the server wants that) one that hops in and out.
    private async Task<bool> PlayInAsync(ulong guildId, ulong channelId, Sound sound, SoundRules rules)
    {
        if (Decoded(sound) is not var (stereo, length))
            return false;
        try
        {
            if (mouths.In(guildId, channelId) is not null)
                return await speech.PlayAsync(guildId, channelId, stereo, length, dip: false);
            return rules.NoHelper == NoHelperChoices.HopIn && await hops.PlayAsync(guildId, channelId, _ => Task.FromResult<(short[], TimeSpan)?>((stereo, length)), $"sound {sound.Name}");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or RestException or WebSocketException)
        {
            logger.LogWarning("Playing sound {Sound} in {ChannelId} of {GuildId} failed: {Message}", sound.Name, channelId, guildId, ex.Message);
            return false;
        }
    }

    private (short[] Stereo, TimeSpan Length)? Decoded(Sound sound)
    {
        if (_decoded.TryGetValue(sound.Id, out var known))
            return known;
        if (sound.Audio is not { } audio || SoundDecoder.Decode(audio, sound.FileName) is not { } spoken)
            return null;
        if (_decoded.Count >= MostKept)
            _decoded.Clear();
        return _decoded[sound.Id] = (Pcm.ToDiscord(spoken), spoken.Length);
    }

    // Counted, and the creator's royalty when someone else plays it (up to the daily cap).
    private async Task CountPlayAsync(Sound sound, ulong userId, bool onDiscord = false)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.SoundPlays.Add(new() { SoundId = sound.Id, GuildId = sound.GuildId, UserId = userId, At = time.GetUtcNow(), OnDiscord = onDiscord });
            await db.SaveChangesAsync();
            await db.Sounds.Where(s => s.Id == sound.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Plays, x => x.Plays + 1).SetProperty(x => x.LastPlayedAt, time.GetUtcNow()));
        }
        var votes = await settings.GetAsync<ExpressionRules>(sound.GuildId, ExpressionShelf.ModuleId);
        if (userId == sound.CreatorId || votes.RoyaltyPerUse <= 0 || !await points.ChargesAsync(sound.GuildId))
            return;
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var (day, paid) = _royalties.GetValueOrDefault((sound.GuildId, sound.CreatorId));
        if (day != today)
            paid = 0;
        var amount = Math.Min(votes.RoyaltyPerUse, votes.RoyaltyDailyCap - paid);
        if (amount <= 0)
            return;
        _royalties[(sound.GuildId, sound.CreatorId)] = (today, paid + amount);
        await points.AwardAsync(sound.GuildId, sound.CreatorId, amount, PointEntryKinds.Royalty, $"someone played your sound {sound.Name}");
    }

    // Discord's own soundboard: what it takes, and its slots by boost level.
    private const int DiscordBytes = 512 * 1024;
    private const int DiscordMilliseconds = 5200;

    private int DiscordSlots(ulong guildId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? (int)guild.PremiumTier switch { 1 => 24, 2 => 36, 3 => 48, _ => 8 } : 8;

    // Puts a library sound on Discord's soundboard too, or takes it off; the link is only as good as Discord's list.
    public async Task<string> OnDiscordAsync(ulong guildId, string name, bool on)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await FindAsync(guildId, name) is not { } found || await db.Sounds.FindAsync(found.Id) is not { } sound)
            return $"There's no sound called `{name}`.";
        if (await discord.SoundsAsync(guildId) is not { } current)
            return "Discord didn't list its soundboard: I need the Manage Expressions permission.";
        var there = sound.DiscordId is { } linked && current.Any(c => c.Id == linked);
        if (!on)
        {
            if (!there)
            {
                var wasLinked = sound.DiscordId is not null;
                sound.DiscordId = null;
                await db.SaveChangesAsync();
                return wasLinked ? $"`{sound.Name}` was taken off Discord's soundboard there already; noted." : $"`{sound.Name}` isn't on Discord's soundboard.";
            }
            if (!await discord.RemoveSoundAsync(guildId, sound.DiscordId!.Value))
                return "Discord didn't let me remove it: I need the Manage Expressions permission.";
            (sound.DiscordId, sound.Pinned, sound.DiscordRemovedAt) = (null, false, time.GetUtcNow());
            await db.SaveChangesAsync();
            return $"`{sound.Name}` is off Discord's soundboard (still in the library), and stays off until pinned again.";
        }
        if (there)
        {
            if (sound.Pinned)
                return $"`{sound.Name}` is pinned on Discord's soundboard already.";
            sound.Pinned = true;
            await db.SaveChangesAsync();
            return $"`{sound.Name}` is pinned on Discord's soundboard: it stays whatever the plays say.";
        }
        if (!sound.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && !sound.FileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            return "Discord's soundboard only takes MP3 or OGG files; this one stays in the library.";
        if (sound.Audio is null || sound.Audio.Length > DiscordBytes || sound.Milliseconds > DiscordMilliseconds)
            return $"Discord's soundboard takes sounds of at most 5.2 s and 512 KB; `{sound.Name}` is {sound.Milliseconds / 1000.0:0.#} s, {(sound.Audio?.Length ?? 0) / 1024} KB.";
        // Whether the board is full is Discord's call: the slots by boost level haven't always matched what it allows.
        if (await discord.AddSoundAsync(guildId, sound.Name, sound.Audio, sound.FileName) is not { } added)
            return current.Count >= DiscordSlots(guildId)
                ? $"Discord didn't take it: its soundboard may be full ({current.Count} sounds there), or it doesn't like the file."
                : "Discord didn't take it (it may not like the file); it stays in the library.";
        sound.DiscordId = added.Id;
        sound.DiscordRemovedAt = null;
        sound.Pinned = true;
        await db.SaveChangesAsync();
        return $"`{sound.Name}` is on Discord's soundboard too, pinned there.";
    }

    // Votes that ran out without enough 👍.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        presence.Changed += ArrivedAsync;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    foreach (var sound in await db.Sounds.Where(s => s.State == SoundStates.Voting && s.VoteEndsAt <= time.GetUtcNow()).ToListAsync(stoppingToken))
                        await DecideAsync(db, sound, accepted: false, await settings.GetAsync<ExpressionRules>(sound.GuildId, ExpressionShelf.ModuleId));
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Closing sound votes failed");
            }
            if (time.GetUtcNow() - _upkeepAt >= Upkeep)
            {
                _upkeepAt = time.GetUtcNow();
                await UpkeepAsync(stoppingToken);
            }
        }
    }

    private async Task DecideAsync(BotDbContext db, Sound sound, bool accepted, ExpressionRules votes)
    {
        sound.State = accepted ? SoundStates.Library : SoundStates.Rejected;
        sound.DecidedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        if (accepted && sound.Paid > 0)
            await points.RefundAsync(sound.GuildId, sound.ProposerId, sound.Paid, $"sound {sound.Name} accepted");
        if (accepted && votes.AcceptBonus > 0 && await points.ChargesAsync(sound.GuildId))
            await points.AwardAsync(sound.GuildId, sound.CreatorId, votes.AcceptBonus, PointEntryKinds.Expression, $"sound {sound.Name} accepted");
        var (ups, downs) = await CountAsync(db, sound.Id);
        await RenderAsync(sound, ups, downs, votes);
        await notifier.NotifyAsync(sound.GuildId, NotificationTopics.EmojiDecisions, [sound.ProposerId],
            Proposals.Decided("sound", sound.Name, accepted, ": it's in the library"),
            sound.VoteChannelId is { } channel ? Notifier.Link(sound.GuildId, channel, sound.VoteMessageId) : await forum.LinkAsync(sound.GuildId, HappeningKinds.Sound, sound.Id));
    }

    // The sound goes with the forum post, to be heard there.
    private static HappeningCard Card(Sound sound, int ups, int downs, ExpressionRules votes)
        => new($"sound: {sound.Name}", [Embed(sound, ups, downs, votes)], [Buttons(sound, votes)], sound.Audio is { } audio ? (sound.FileName, audio) : null);

    // Every copy of the card: where it was proposed, and in the forum.
    private async Task RenderAsync(Sound sound, int ups, int downs, ExpressionRules votes)
    {
        await forum.ShowAsync(sound.GuildId, HappeningKinds.Sound, sound.Id, Proposals.HappeningState(sound.State == SoundStates.Voting), Card(sound, ups, downs, votes));
        if (sound is not { VoteChannelId: { } channelId, VoteMessageId: { } messageId })
            return;
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m => (m.Embeds, m.Components) = ([Embed(sound, ups, downs, votes)], [Buttons(sound, votes)]));
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task<(int Ups, int Downs)> CountAsync(BotDbContext db, long id)
    {
        var votes = await db.SoundVotes.Where(v => v.SoundId == id).Select(v => v.Up).ToListAsync();
        return (votes.Count(up => up), votes.Count(up => !up));
    }

    private static EmbedProperties Embed(Sound s, int ups, int downs, ExpressionRules votes) => new()
    {
        Title = $"🔊 {s.Name} ({s.Milliseconds / 1000.0:0.#} s)",
        Description = $"Proposed by <@{s.ProposerId}>\n" + s.State switch
        {
            SoundStates.Voting => Proposals.VotingStatus(ups, downs, votes, s.VoteEndsAt),
            SoundStates.Library => $"{Proposals.Outcome(true, ups, downs, votes)}: `/sound play {s.Name}`.",
            _ => $"{Proposals.Outcome(false, ups, downs, votes)}.",
        },
        Color = s.State == SoundStates.Voting ? new(0xF1C40F) : s.State == SoundStates.Library ? new(0x57F287) : new(0x99AAB5),
    };

    private static ActionRowProperties Buttons(Sound s, ExpressionRules votes) => Proposals.Buttons("soundvote", s.Id, s.State == SoundStates.Voting, votes);

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    [GeneratedRegex(@"^[\p{L}\p{N} _-]{2,32}$")]
    private static partial Regex ValidName();
}
