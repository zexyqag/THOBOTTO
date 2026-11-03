using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Kaomoji;

// A face someone posted with /kaomoji, in any server or DM: how often, and when last.
public sealed class KaomojiUse
{
    public ulong UserId { get; init; }

    public required string Text { get; init; }

    public int Uses { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }
}

// Someone's favourite faces: those they post most, then latest, offered first as they type. Only the ones they used
// last are kept, so a face they've moved on from goes.
public sealed class KaomojiFavourites(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    public const int Kept = 50;

    public async Task<IReadOnlyList<KaomojiUse>> OfAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.KaomojiUses.AsNoTracking().Where(u => u.UserId == userId)
            .OrderByDescending(u => u.Uses).ThenByDescending(u => u.LastUsedAt).ToListAsync();
    }

    public async Task<IReadOnlyList<string>> TextsAsync(ulong userId) => [.. (await OfAsync(userId)).Select(u => u.Text)];

    public async Task UsedAsync(ulong userId, string text)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        if (await db.KaomojiUses.FindAsync(userId, text) is { } known)
            (known.Uses, known.LastUsedAt) = (known.Uses + 1, now);
        else
            db.KaomojiUses.Add(new() { UserId = userId, Text = text, Uses = 1, LastUsedAt = now });
        await db.SaveChangesAsync();
        await db.KaomojiUses.Where(u => u.UserId == userId).OrderByDescending(u => u.LastUsedAt).Skip(Kept).ExecuteDeleteAsync();
    }

    // One of them, or all with no text.
    public async Task<bool> ForgetAsync(ulong userId, string? text = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.KaomojiUses.Where(u => u.UserId == userId && (text == null || u.Text == text)).ExecuteDeleteAsync() > 0;
    }
}
