using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Sounds;

public sealed partial class SoundBoard
{
    private readonly DailySwaps _swaps = new(time);

    // Plays per library sound since a moment, by helpers and from Discord's board alike.
    public static async Task<IReadOnlyDictionary<long, int>> PlaysSinceAsync(BotDbContext db, ulong guildId, DateTimeOffset since, CancellationToken ct = default)
        => await db.SoundPlays.Where(p => p.GuildId == guildId && p.At >= since).GroupBy(p => p.SoundId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    private static bool FitsDiscord(string fileName, int audioLength, int milliseconds)
        => (fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            && audioLength > 0 && audioLength <= DiscordBytes && milliseconds <= DiscordMilliseconds;

    // The library as the policy sees it: on the board when Discord's list (or, for the page, the link kept here) has it.
    private static async Task<List<RankedItem>> RankedAsync(BotDbContext db, ulong guildId, SoundRules rules, DateTimeOffset now, Func<ulong, bool> onBoard, CancellationToken ct)
    {
        var plays = await PlaysSinceAsync(db, guildId, now - TimeSpan.FromDays(rules.MostPlayedDays), ct);
        var trialSince = now - TimeSpan.FromDays(rules.TrialDays);
        var rows = await db.Sounds.AsNoTracking()
            .Where(s => s.GuildId == guildId && s.State == SoundStates.Library)
            .Select(s => new { s.Id, s.Name, s.Pinned, s.DiscordId, s.DiscordRemovedAt, s.DecidedAt, s.FileName, s.Milliseconds, AudioLength = s.Audio == null ? 0 : s.Audio.Length })
            .ToListAsync(ct);
        return [.. rows.Select(r => new RankedItem(r.Name, new SlotItem(r.Id, r.Pinned, r.DiscordId is { } id && onBoard(id), FitsDiscord(r.FileName, r.AudioLength, r.Milliseconds),
            r.DiscordRemovedAt is null, plays.GetValueOrDefault(r.Id), rules.TrialDays > 0 && r.DecidedAt >= trialSince)))];
    }

    private int SwapsLeft(ulong guildId, SoundRules rules) => _swaps.Left(guildId, rules.SwapsPerDay);

    // For the Sounds page: the ranking by plays the most-played policy works from, the board's slots and today's swaps.
    public async Task<SlotView> BoardViewAsync(ulong guildId)
    {
        var rules = await RulesAsync(guildId);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var ranked = await RankedAsync(db, guildId, rules, now, _ => true, CancellationToken.None);
        var used = ranked.Count(r => r.Item.OnDiscord);
        return new(SlotPolicy.Rank(ranked), rules.MostPlayedDays, Math.Max(DiscordSlots(guildId), used), used, SwapsLeft(guildId, rules), rules.SwapsPerDay);
    }

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
            var sounds = (await RankedAsync(db, guildId, rules, now, there.Contains, ct)).Select(r => r.Item).ToList();
            var plan = SlotPolicy.Plan(sounds, Math.Max(0, DiscordSlots(guildId) - current.Count), SwapsLeft(guildId, rules), rules.SwapMargin);
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
        _swaps.Made(guildId, swaps);
    }
}
