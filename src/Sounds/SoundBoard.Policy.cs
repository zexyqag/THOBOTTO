using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Sounds;

// A library sound as the most-played policy sees it.
public sealed record BoardSound(long Id, bool Pinned, bool OnBoard, bool Fits, bool Wanted, int Plays, bool Trial);

// What to take off Discord's soundboard and what to put on, in that order.
public sealed record BoardPlan(IReadOnlyList<long> Pull, IReadOnlyList<long> Push);

public static class SoundBoardPolicy
{
    // Pinned sounds come first, then new ones on trial, then by plays; free slots are filled, and after that a
    // sound replaces the least played unpinned one only with clearly more plays, up to the day's swaps. Trial
    // sounds take free slots and aren't swapped out while on trial.
    public static BoardPlan Plan(IReadOnlyList<BoardSound> sounds, int freeSlots, int swapsLeft, int marginPercent)
    {
        var candidates = sounds
            .Where(s => !s.OnBoard && s.Fits && (s.Pinned || s.Wanted))
            .OrderByDescending(s => s.Pinned).ThenByDescending(s => s.Trial).ThenByDescending(s => s.Plays).ThenBy(s => s.Id)
            .ToList();
        var weakestFirst = sounds.Where(s => s.OnBoard && !s.Pinned && !s.Trial).OrderBy(s => s.Plays).ThenByDescending(s => s.Id).ToList();
        var pull = new List<long>();
        var push = new List<long>();
        foreach (var candidate in candidates)
        {
            if (freeSlots > 0)
            {
                push.Add(candidate.Id);
                freeSlots--;
                continue;
            }
            if (swapsLeft <= 0 || weakestFirst.Count == 0)
                break;
            var weakest = weakestFirst[0];
            if (!candidate.Pinned && (candidate.Trial || candidate.Plays * 100 <= weakest.Plays * (100 + marginPercent)))
                break;
            weakestFirst.RemoveAt(0);
            pull.Add(weakest.Id);
            push.Add(candidate.Id);
            swapsLeft--;
        }
        return new(pull, push);
    }
}

public sealed partial class SoundBoard
{
    // (server) → (day, swaps made that day).
    private readonly ConcurrentDictionary<ulong, (DateOnly Day, int Swaps)> _swaps = new();

    // Plays per library sound since a moment, by helpers and from Discord's board alike.
    public static async Task<IReadOnlyDictionary<long, int>> PlaysSinceAsync(BotDbContext db, ulong guildId, DateTimeOffset since, CancellationToken ct = default)
        => await db.SoundPlays.Where(p => p.GuildId == guildId && p.At >= since).GroupBy(p => p.SoundId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    private static bool FitsDiscord(string fileName, int audioLength, int milliseconds)
        => (fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            && audioLength > 0 && audioLength <= DiscordBytes && milliseconds <= DiscordMilliseconds;

    // Discord's soundboard filled with the most played sounds around the pinned ones.
    private async Task KeepBoardAsync(ulong guildId, SoundRules rules, CancellationToken ct)
    {
        if (await discord.SoundsAsync(guildId) is not { } current)
            return;
        var now = time.GetUtcNow();
        var there = current.Select(c => c.Id).ToHashSet();
        List<long> pull, push;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var plays = await PlaysSinceAsync(db, guildId, now - TimeSpan.FromDays(rules.MostPlayedDays), ct);
            var trialSince = now - TimeSpan.FromDays(rules.TrialDays);
            var rows = await db.Sounds.AsNoTracking()
                .Where(s => s.GuildId == guildId && s.State == SoundStates.Library)
                .Select(s => new { s.Id, s.Pinned, s.DiscordId, s.DiscordRemovedAt, s.DecidedAt, s.FileName, s.Milliseconds, AudioLength = s.Audio == null ? 0 : s.Audio.Length })
                .ToListAsync(ct);
            var sounds = rows.Select(r => new BoardSound(r.Id, r.Pinned, r.DiscordId is { } id && there.Contains(id), FitsDiscord(r.FileName, r.AudioLength, r.Milliseconds),
                r.DiscordRemovedAt is null, plays.GetValueOrDefault(r.Id), rules.TrialDays > 0 && r.DecidedAt >= trialSince)).ToList();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var (day, swapped) = _swaps.GetValueOrDefault(guildId);
            var swapsLeft = rules.SwapsPerDay - (day == today ? swapped : 0);
            var plan = SoundBoardPolicy.Plan(sounds, Math.Max(0, DiscordSlots(guildId) - current.Count), swapsLeft, rules.SwapMargin);
            pull = [.. plan.Pull];
            push = [.. plan.Push];
        }
        if (pull.Count == 0 && push.Count == 0)
            return;

        await using var work = await dbFactory.CreateDbContextAsync(ct);
        var swaps = 0;
        foreach (var sound in await work.Sounds.Where(s => pull.Contains(s.Id) && s.DiscordId != null).ToListAsync(ct))
        {
            if (!await discord.RemoveSoundAsync(guildId, sound.DiscordId!.Value))
                break;
            sound.DiscordId = null;
            swaps++;
            logger.LogInformation("Took {Name} off Discord's soundboard of {GuildId} for a more played sound", sound.Name, guildId);
        }
        await work.SaveChangesAsync(ct);
        foreach (var sound in await work.Sounds.Where(s => push.Contains(s.Id) && s.DiscordId == null && s.Audio != null).ToListAsync(ct))
        {
            if (await discord.AddSoundAsync(guildId, sound.Name, sound.Audio!, sound.FileName) is not { } added)
            {
                logger.LogInformation("Discord didn't take {Name} for the soundboard of {GuildId} (full, or not the file for it)", sound.Name, guildId);
                break;
            }
            sound.DiscordId = added.Id;
            logger.LogInformation("Put {Name} on Discord's soundboard of {GuildId} by plays", sound.Name, guildId);
        }
        await work.SaveChangesAsync(ct);
        if (swaps > 0)
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            _swaps.AddOrUpdate(guildId, (today, swaps), (_, old) => (today, (old.Day == today ? old.Swaps : 0) + swaps));
        }
    }
}
