using System.Collections.Concurrent;

namespace THOBOTTO.Discord;

// A library item (a sound, an emoji) as the most-used policy for Discord's limited slots sees it.
public sealed record SlotItem(long Id, bool Pinned, bool OnDiscord, bool Fits, bool Wanted, int Uses, bool Trial);

// What to take off Discord and what to put on, in that order.
public sealed record SlotPlan(IReadOnlyList<long> Pull, IReadOnlyList<long> Push);

// A library item in the ranking a page shows: its uses in the policy's window and what the policy knows of it.
public sealed record RankedItem(string Name, SlotItem Item);

// Discord's slots as the most-used policy sees them, for a page.
public sealed record SlotView(IReadOnlyList<RankedItem> Ranking, int Days, int Slots, int Used, int SwapsLeft, int SwapsPerDay);

public static class SlotPolicy
{
    // Pinned items come first, then new ones on trial, then by uses; free slots are filled, and after that an item
    // replaces the least used unpinned one only with clearly more uses, up to the day's swaps. Trial items take free
    // slots and aren't swapped out while on trial.
    public static SlotPlan Plan(IReadOnlyList<SlotItem> items, int freeSlots, int swapsLeft, int marginPercent)
    {
        var candidates = items
            .Where(s => !s.OnDiscord && s.Fits && (s.Pinned || s.Wanted))
            .OrderByDescending(s => s.Pinned).ThenByDescending(s => s.Trial).ThenByDescending(s => s.Uses).ThenBy(s => s.Id)
            .ToList();
        var weakestFirst = items.Where(s => s.OnDiscord && !s.Pinned && !s.Trial).OrderBy(s => s.Uses).ThenByDescending(s => s.Id).ToList();
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
            if (!candidate.Pinned && (candidate.Trial || candidate.Uses * 100 <= weakest.Uses * (100 + marginPercent)))
                break;
            weakestFirst.RemoveAt(0);
            pull.Add(weakest.Id);
            push.Add(candidate.Id);
            swapsLeft--;
        }
        return new(pull, push);
    }

    // The library by uses in the window, most used first, the same uses the plan goes by.
    public static IReadOnlyList<RankedItem> Rank(IEnumerable<RankedItem> items)
        => [.. items.OrderByDescending(s => s.Item.Uses).ThenByDescending(s => s.Item.Pinned).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
}

// Swaps made per server today, so a policy keeps to its daily most.
public sealed class DailySwaps(TimeProvider time)
{
    private readonly ConcurrentDictionary<ulong, (DateOnly Day, int Swaps)> _made = new();

    public int Left(ulong guildId, int perDay)
    {
        var (day, made) = _made.GetValueOrDefault(guildId);
        return Math.Max(0, perDay - (day == Today ? made : 0));
    }

    public void Made(ulong guildId, int swaps)
    {
        if (swaps > 0)
            _made.AddOrUpdate(guildId, (Today, swaps), (_, old) => (Today, (old.Day == Today ? old.Swaps : 0) + swaps));
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
