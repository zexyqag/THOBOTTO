using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Expressions;
using THOBOTTO.Helpers;
using THOBOTTO.Listening;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;
using THOBOTTO.Speaking;
using THOBOTTO.Voice;

namespace THOBOTTO.Sounds;

// The server's own soundboard: as many sounds as it likes, proposed and voted on like emojis (managers add
// directly), played by a helper in the voice channel over the music, and join sounds for anyone, no Nitro needed.
// Where no helper is in the channel, a free one can hop in to play it. Creators earn royalties on plays.
public sealed partial class SoundBoard(
    IDbContextFactory<BotDbContext> dbFactory,
    RestClient rest,
    GatewayClient gateway,
    THOBOTTO.Discord.DiscordApi discord,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    Notifier notifier,
    HelperFleet fleet,
    ListeningSeats seats,
    VoicePresence presence,
    VoiceMouths mouths,
    HelperSpeech speech,
    TimeProvider time,
    ILogger<SoundBoard> logger) : BackgroundService
{
    public const string ModuleId = "sounds";
    public const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);
    private const int MostKept = 100;

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Decoded sounds, ready to play.
    private readonly ConcurrentDictionary<long, (short[] Stereo, TimeSpan Length)> _decoded = new();
    // (server, member) → when they last played one, or their join sound last played.
    private readonly ConcurrentDictionary<(ulong, ulong), DateTimeOffset> _played = new();
    private readonly ConcurrentDictionary<(ulong, ulong), DateTimeOffset> _joined = new();
    // Helpers hopping in right now.
    private readonly ConcurrentDictionary<ulong, byte> _hopping = new();
    // (server, creator) → (day, royalties paid that day).
    private readonly ConcurrentDictionary<(ulong, ulong), (DateOnly Day, double Paid)> _royalties = new();

    public ValueTask<SoundRules> RulesAsync(ulong guildId) => settings.GetAsync<SoundRules>(guildId, ModuleId);

    public async Task<IReadOnlyList<Sound>> LibraryAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId && s.State == SoundStates.Library).OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<Sound?> FindAsync(ulong guildId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var library = await db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId && s.State == SoundStates.Library).ToListAsync();
        return library.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? library.Select(s => (Sound: s, Score: Assistant.Likeness.Of(name, s.Name))).Where(s => s.Score >= 0.7).OrderByDescending(s => s.Score).Select(s => s.Sound).FirstOrDefault();
    }

    // A proposal (voted on in the emoji and sticker voting channel), or straight into the library for managers.
    public async Task<string> AddAsync(ulong guildId, string name, ulong userId, Attachment file, bool direct)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return $"The `{ModuleId}` module is off.";
        name = name.Trim();
        if (!ValidName().IsMatch(name))
            return "Sound names are 2 to 32 letters, digits, spaces, - or _.";
        if (!SoundDecoder.Known(file.FileName))
            return "Sounds must be MP3, OGG or WAV.";
        if (file.Size > MaxBytes)
            return $"That file is {file.Size / 1024} KB; sounds can be at most {MaxBytes / 1024 / 1024} MB.";
        var bytes = await Http.GetByteArrayAsync(file.Url);
        if (SoundDecoder.Decode(bytes, file.FileName) is not { } decoded)
            return "I couldn't read that as audio.";
        var rules = await RulesAsync(guildId);
        if (decoded.Length.TotalSeconds > rules.MaxSeconds)
            return $"That's {decoded.Length.TotalSeconds:0.#} s; sounds here can be at most {rules.MaxSeconds} s.";
        return await OpenAsync(guildId, name, userId, bytes, file.FileName, (int)decoded.Length.TotalMilliseconds, direct, null);
    }

    private async Task<string> OpenAsync(ulong guildId, string name, ulong userId, byte[] bytes, string fileName, int ms, bool direct, ulong? discordId)
    {
        var votes = await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId);
        if (!direct && votes.VoteChannelId is null)
            return "There's no voting channel yet; it's the one for emojis and stickers (`/setup emojis`).";
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

            var message = await rest.SendMessageAsync(votes.VoteChannelId!.Value, new()
            {
                Content = Status(sound, 0, 0, votes),
                Attachments = [new AttachmentProperties(fileName, new MemoryStream(bytes))],
                Components = [Buttons(sound)],
                AllowedMentions = AllowedMentionsProperties.None,
            });
            (sound.VoteChannelId, sound.VoteMessageId) = (message.ChannelId, message.Id);
            await db.SaveChangesAsync();
            return $"Your sound is up for a vote in <#{votes.VoteChannelId}>.";
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
                return "Voting on this has ended.";
            if (userId == sound.ProposerId)
                return "You put this one up, so your vote doesn't count.";
            if (await db.SoundVotes.FindAsync(id, userId) is { } vote)
                vote.Up = up;
            else
                db.SoundVotes.Add(new() { SoundId = id, UserId = userId, Up = up });
            await db.SaveChangesAsync();
            var votes = await settings.GetAsync<ExpressionRules>(sound.GuildId, ExpressionShelf.ModuleId);
            var (ups, downs) = await CountAsync(db, id);
            if (ups - downs >= votes.VoteMargin)
                await DecideAsync(db, sound, accepted: true, votes);
            else
                await RenderAsync(sound, ups, downs, votes);
            return up ? "Voted 👍." : "Voted 👎.";
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
        await db.JoinSounds.Where(j => j.SoundId == sound.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        _decoded.TryRemove(sound.Id, out _);
        return $"Removed `{sound.Name}`.";
    }

    // Plays a sound in the member's voice channel; the answer to give them.
    public async Task<string> PlayAsync(ulong guildId, ulong userId, string name)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return $"The `{ModuleId}` module is off.";
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var where))
            return "Join a voice channel first.";
        var rules = await RulesAsync(guildId);
        if (_played.TryGetValue((guildId, userId), out var last) && time.GetUtcNow() - last < TimeSpan.FromSeconds(rules.CooldownSeconds))
            return $"Wait a moment between sounds ({rules.CooldownSeconds} s).";
        if (await FindAsync(guildId, name) is not { } sound)
            return $"There's no sound called `{name}`. `/sound list` shows them.";
        _played[(guildId, userId)] = time.GetUtcNow();
        if (!await PlayInAsync(guildId, where.ChannelId, sound, rules))
            return "No helper is free to play it there.";
        await CountPlayAsync(sound, userId);
        return $"🔊 {sound.Name}";
    }

    // A moment for a hub to move them to their own channel: the sound plays where they are by then.
    private static readonly TimeSpan JoinSettle = TimeSpan.FromSeconds(1.5);

    // Someone (not a bot) arrived in a voice channel.
    public async Task JoinedAsync(ulong guildId, ulong userId, ulong channelId)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return;
        await Task.Delay(JoinSettle, time);
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var now))
            return;
        channelId = now.ChannelId;
        var rules = await RulesAsync(guildId);
        if (!rules.JoinSounds || _joined.TryGetValue((guildId, userId), out var last) && time.GetUtcNow() - last < TimeSpan.FromMinutes(rules.JoinCooldownMinutes))
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.JoinSounds.FindAsync(guildId, userId) is not { } chosen || await db.Sounds.AsNoTracking().FirstOrDefaultAsync(s => s.Id == chosen.SoundId && s.State == SoundStates.Library) is not { } sound)
            return;
        _joined[(guildId, userId)] = time.GetUtcNow();
        await PlayInAsync(guildId, channelId, sound, rules);
    }

    public async Task<string> SetJoinSoundAsync(ulong guildId, ulong userId, string? name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var current = await db.JoinSounds.FindAsync(guildId, userId);
        if (name is null)
        {
            if (current is not null)
                db.JoinSounds.Remove(current);
            await db.SaveChangesAsync();
            return "No join sound for you any more.";
        }
        if (await FindAsync(guildId, name) is not { } sound)
            return $"There's no sound called `{name}`.";
        if (current is null)
            db.JoinSounds.Add(new() { GuildId = guildId, UserId = userId, SoundId = sound.Id });
        else
            current.SoundId = sound.Id;
        await db.SaveChangesAsync();
        return $"Your join sound is `{sound.Name}`.";
    }

    public async Task<Sound?> JoinSoundOfAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.JoinSounds.FindAsync(guildId, userId) is { } chosen ? await db.Sounds.AsNoTracking().FirstOrDefaultAsync(s => s.Id == chosen.SoundId) : null;
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
            return rules.NoHelper == NoHelperChoices.HopIn && await HopInAsync(guildId, channelId, stereo, length);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or RestException or System.Net.WebSockets.WebSocketException)
        {
            logger.LogWarning("Playing sound {Sound} in {ChannelId} failed: {Message}", sound.Name, channelId, ex.Message);
            return false;
        }
    }

    private async Task<bool> HopInAsync(ulong guildId, ulong channelId, short[] stereo, TimeSpan length)
    {
        var present = presence.Snapshot(guildId);
        var helper = fleet.Helpers.FirstOrDefault(h => h.InGuild(guildId) && !h.Players.ContainsKey(guildId) && !seats.IsListening(guildId, h.UserId)
            && !present.ContainsKey(h.UserId) && _hopping.TryAdd(h.UserId, 0));
        if (helper is null)
            return false;
        VoiceClient? client = null;
        try
        {
            client = await helper.Gateway.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration()).WaitAsync(Slack);
            await client.StartAsync();
            await client.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
            await mouths.Of(client).SpeakAsync(stereo, dip: false).WaitAsync(length + Slack);
            logger.LogInformation("{Helper} hopped into {ChannelId} to play a sound", helper.Name, channelId);
            return true;
        }
        finally
        {
            if (client is not null)
            {
                mouths.Forget(client);
                client.Dispose();
            }
            await helper.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
            _hopping.TryRemove(helper.UserId, out _);
        }
    }

    private (short[] Stereo, TimeSpan Length)? Decoded(Sound sound)
    {
        if (_decoded.TryGetValue(sound.Id, out var known))
            return known;
        if (SoundDecoder.Decode(sound.Audio, sound.FileName) is not { } spoken)
            return null;
        if (_decoded.Count >= MostKept)
            _decoded.Clear();
        return _decoded[sound.Id] = (Pcm.ToDiscord(spoken), spoken.Length);
    }

    // Counted, and the creator's royalty when someone else plays it (up to the daily cap).
    private async Task CountPlayAsync(Sound sound, ulong userId)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.Sounds.Where(s => s.Id == sound.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Plays, x => x.Plays + 1).SetProperty(x => x.LastPlayedAt, time.GetUtcNow()));
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

    // Puts a library sound on Discord's soundboard too, or takes it off.
    public async Task<string> OnDiscordAsync(ulong guildId, string name, bool on)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await FindAsync(guildId, name) is not { } found || await db.Sounds.FindAsync(found.Id) is not { } sound)
            return $"There's no sound called `{name}`.";
        if (!on)
        {
            if (sound.DiscordId is not { } discordId)
                return $"`{sound.Name}` isn't on Discord's soundboard.";
            if (!await discord.RemoveSoundAsync(guildId, discordId))
                return "Discord didn't let me remove it: I need the Manage Expressions permission.";
            sound.DiscordId = null;
            await db.SaveChangesAsync();
            return $"`{sound.Name}` is off Discord's soundboard (still in the library).";
        }
        if (sound.DiscordId is not null)
            return $"`{sound.Name}` is on Discord's soundboard already.";
        if (!sound.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && !sound.FileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            return "Discord's soundboard only takes MP3 or OGG files; this one stays in the library.";
        if (sound.Audio.Length > DiscordBytes || sound.Milliseconds > DiscordMilliseconds)
            return $"Discord's soundboard takes sounds of at most 5.2 s and 512 KB; `{sound.Name}` is {sound.Milliseconds / 1000.0:0.#} s, {sound.Audio.Length / 1024} KB.";
        if (await discord.SoundsAsync(guildId) is not { } current)
            return "Discord didn't list its soundboard: I need the Manage Expressions permission.";
        if (current.Count >= DiscordSlots(guildId))
            return $"Discord's soundboard is full ({current.Count} of {DiscordSlots(guildId)}): take one off first.";
        if (await discord.AddSoundAsync(guildId, sound.Name, sound.Audio, sound.FileName) is not { } added)
            return "Discord didn't take it (it may not like the file); it stays in the library.";
        sound.DiscordId = added.Id;
        await db.SaveChangesAsync();
        return $"`{sound.Name}` is on Discord's soundboard too.";
    }

    // Discord's soundboard sounds into the library (those it doesn't have yet), and off-Discord marks for those
    // removed there.
    public async Task<string> ImportAsync(ulong guildId, ulong actorId)
    {
        if (await discord.SoundsAsync(guildId) is not { } current)
            return "Discord didn't list its soundboard: I need the Manage Expressions permission.";
        await using var db = await dbFactory.CreateDbContextAsync();
        var mine = await db.Sounds.Where(s => s.GuildId == guildId && s.State == SoundStates.Library).ToListAsync();
        var gone = mine.Where(s => s.DiscordId is { } id && current.All(c => c.Id != id)).ToList();
        foreach (var sound in gone)
            sound.DiscordId = null;
        await db.SaveChangesAsync();

        var added = 0;
        foreach (var remote in current.Where(c => mine.All(s => s.DiscordId != c.Id)))
        {
            if (await discord.SoundFileAsync(remote.Id) is not { } bytes)
                continue;
            var fileName = bytes.AsSpan().StartsWith("OggS"u8) ? $"{remote.Id}.ogg" : $"{remote.Id}.mp3";
            if (SoundDecoder.Decode(bytes, fileName) is not { } decoded)
                continue;
            var name = mine.Any(s => string.Equals(s.Name, remote.Name, StringComparison.OrdinalIgnoreCase)) ? $"{remote.Name} (Discord)" : remote.Name;
            if (await OpenAsync(guildId, name, remote.UserId ?? actorId, bytes, fileName, (int)decoded.Length.TotalMilliseconds, direct: true, remote.Id) is { } result && result.StartsWith("🔊"))
                added++;
        }
        return $"Imported {added} of Discord's {current.Count} soundboard sounds{(gone.Count > 0 ? $"; {gone.Count} removed on Discord are marked off it" : "")}.";
    }

    // Votes that ran out without enough 👍.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            $"your sound `{sound.Name}` was {(accepted ? "accepted: it's in the library" : "not accepted")}",
            sound.VoteChannelId is { } channel ? Notifier.Link(sound.GuildId, channel, sound.VoteMessageId) : null);
    }

    private async Task RenderAsync(Sound sound, int ups, int downs, ExpressionRules votes)
    {
        if (sound is not { VoteChannelId: { } channelId, VoteMessageId: { } messageId })
            return;
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m => (m.Content, m.Components) = (Status(sound, ups, downs, votes), [Buttons(sound)]));
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

    private static string Status(Sound s, int ups, int downs, ExpressionRules votes) => $"🔊 **{s.Name}** ({s.Milliseconds / 1000.0:0.#} s), proposed by <@{s.ProposerId}>\n" + s.State switch
    {
        SoundStates.Voting => $"👍 {ups} · 👎 {downs}. Needs {votes.VoteMargin} more 👍 than 👎, voting ends <t:{s.VoteEndsAt.ToUnixTimeSeconds()}:R>.",
        SoundStates.Library => $"Accepted ({ups}–{downs}): `/sound play {s.Name}`.",
        _ => $"Not accepted ({ups}–{downs}).",
    };

    private static ActionRowProperties Buttons(Sound s) => new()
    {
        new ButtonProperties($"soundvote:{s.Id}:1", EmojiProperties.Standard("👍"), ButtonStyle.Success) { Disabled = s.State != SoundStates.Voting },
        new ButtonProperties($"soundvote:{s.Id}:0", EmojiProperties.Standard("👎"), ButtonStyle.Danger) { Disabled = s.State != SoundStates.Voting },
    };

    [GeneratedRegex(@"^[\p{L}\p{N} _-]{2,32}$")]
    private static partial Regex ValidName();
}
