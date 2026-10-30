using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Sounds;

// Discord's soundboard as a view of the library: what's uploaded there comes in, names follow both ways, a sound
// gone there is noted, and plays there count. Looked at when Discord says the soundboard changed, every quarter
// hour, and on /sound import.
public sealed partial class SoundBoard
{
    private static readonly TimeSpan Upkeep = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SettleAfterChange = TimeSpan.FromSeconds(3);
    private DateTimeOffset _upkeepAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<ulong, SyncReport> _sync = new();
    private readonly ConcurrentDictionary<ulong, byte> _changed = new();

    public SyncReport? LastSync(ulong guildId) => _sync.GetValueOrDefault(guildId);

    // Discord said its soundboard changed; a moment later (several events come at once) the library follows.
    public void DiscordChanged(ulong guildId)
    {
        if (!_changed.TryAdd(guildId, 0))
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(SettleAfterChange, time);
            _changed.TryRemove(guildId, out _);
            try
            {
                if (await KeptInStepAsync(guildId))
                    await ReconcileAsync(guildId, OwnerOf(guildId));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Following Discord's soundboard change in {GuildId} failed", guildId);
            }
        });
    }

    // A sound from Discord's own soundboard played in a voice channel counts as a play of the library's copy.
    public async Task OnDiscordPlayAsync(ulong guildId, ulong userId, ulong soundId)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Sounds.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId && s.DiscordId == soundId && s.State == SoundStates.Library) is { } sound)
            await CountPlayAsync(sound, userId, onDiscord: true);
    }

    private async Task<bool> KeptInStepAsync(ulong guildId)
        => await modules.IsEnabledAsync(guildId, ModuleId) && (await RulesAsync(guildId)).DiscordSync;

    private ulong OwnerOf(ulong guildId) => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.OwnerId : 0;

    // The library and Discord's soundboard brought in step; what was done.
    public async Task<SyncReport> ReconcileAsync(ulong guildId, ulong actorId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (await discord.SoundsAsync(guildId) is not { } current)
        {
            if (_sync.GetValueOrDefault(guildId)?.NoAccess != true)
                logger.LogWarning("Discord didn't list the soundboard of {GuildId}: the bot needs Manage Expressions there", guildId);
            return _sync[guildId] = new(0, DiscordSlots(guildId), 0, 0, 0, 0, true, now);
        }

        List<Sound> rows;
        SyncPlan plan;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            rows = await db.Sounds.Where(s => s.GuildId == guildId && (s.State == SoundStates.Library || s.State == SoundStates.Voting || s.DiscordId != null)).ToListAsync(ct);
            plan = SoundSync.Plan(current, rows);
            foreach (var sound in rows.Where(s => plan.TakenOff.Contains(s.Id)))
                (sound.DiscordId, sound.DiscordRemovedAt) = (null, now);
            foreach (var (id, name) in plan.Renames)
                rows.First(s => s.Id == id).Name = name;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        int imported = 0, linked = 0;
        foreach (var remote in plan.Unknown)
        {
            if (await discord.SoundFileAsync(remote.Id) is not { } bytes)
                continue;
            var fileName = bytes.AsSpan().StartsWith("OggS"u8) ? $"{remote.Id}.ogg" : $"{remote.Id}.mp3";
            if (SoundDecoder.Decode(bytes, fileName) is not { } decoded)
                continue;
            var ms = (int)decoded.Length.TotalMilliseconds;
            if (SoundSync.Match(remote.Name, ms, rows) is { } same)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.Sounds.Where(s => s.Id == same.Id && s.DiscordId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.DiscordId, remote.Id).SetProperty(x => x.DiscordRemovedAt, (DateTimeOffset?)null), ct);
                same.DiscordId = remote.Id;
                linked++;
                continue;
            }
            var name = rows.Any(s => (s.State is SoundStates.Voting or SoundStates.Library) && string.Equals(s.Name, remote.Name, StringComparison.OrdinalIgnoreCase)) ? $"{remote.Name} (Discord)" : remote.Name;
            if ((await OpenAsync(guildId, name, remote.UserId ?? actorId, bytes, fileName, ms, direct: true, remote.Id)).StartsWith("🔊"))
                imported++;
        }
        var report = new SyncReport(current.Count, Math.Max(DiscordSlots(guildId), current.Count), imported, linked, plan.Renames.Count, plan.TakenOff.Count, false, now);
        if (imported + linked + plan.Renames.Count + plan.TakenOff.Count > 0)
            logger.LogInformation("Sounds of {GuildId} and Discord's soundboard: {Report}", guildId, report.Describe());
        return _sync[guildId] = report;
    }

    // Another name for a library sound, and for its copy on Discord's soundboard.
    public async Task<string> RenameAsync(ulong guildId, string name, string newName)
    {
        newName = newName.Trim();
        if (!IsValidName(newName))
            return "Sound names are 2 to 32 letters, digits, spaces, - or _.";
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await FindAsync(guildId, name) is not { } found || await db.Sounds.FindAsync(found.Id) is not { } sound)
                return $"There's no sound called `{name}`.";
            var lowered = newName.ToLowerInvariant();
            if (await db.Sounds.AnyAsync(s => s.GuildId == guildId && s.Id != sound.Id && s.Name.ToLower() == lowered && (s.State == SoundStates.Voting || s.State == SoundStates.Library)))
                return $"There's already a sound called `{newName}`.";
            var old = sound.Name;
            sound.Name = newName;
            await db.SaveChangesAsync();
            var note = sound.DiscordId is { } discordId && !await discord.RenameSoundAsync(guildId, discordId, newName)
                ? " Discord's copy kept its name: I need the Manage Expressions permission."
                : "";
            return $"`{old}` is now `{newName}`.{note}";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Removed and rejected sounds' audio, dropped once the server's keep time has passed; the record stays.
    public static Task<int> DropAudioAsync(BotDbContext db, ulong guildId, DateTimeOffset before, CancellationToken ct = default)
        => db.Sounds
            .Where(s => s.GuildId == guildId && s.Audio != null && ((s.State == SoundStates.Rejected && s.DecidedAt < before) || (s.State == SoundStates.Removed && s.RemovedAt < before)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Audio, (byte[]?)null), ct);

    private async Task UpkeepAsync(CancellationToken ct)
    {
        foreach (var guildId in gateway.Cache.Guilds.Keys)
        {
            try
            {
                if (!await modules.IsEnabledAsync(guildId, ModuleId))
                    continue;
                var rules = await RulesAsync(guildId);
                if (rules.DiscordSync)
                    await ReconcileAsync(guildId, OwnerOf(guildId), ct);
                if (rules.DiscordBoard == BoardChoices.MostPlayed)
                    await KeepBoardAsync(guildId, rules, ct);
                if (rules.KeepRemovedDays > 0)
                {
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var dropped = await DropAudioAsync(db, guildId, time.GetUtcNow() - TimeSpan.FromDays(rules.KeepRemovedDays), ct);
                    if (dropped > 0)
                        logger.LogInformation("Dropped the audio of {Count} removed or rejected sounds of {GuildId}", dropped, guildId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sound upkeep for {GuildId} failed", guildId);
            }
        }
    }
}
