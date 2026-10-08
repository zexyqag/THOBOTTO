using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Quotes;

public sealed class QuoteBook(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    public async Task<Quote> AddAsync(Quote quote)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var saved = new Quote
        {
            GuildId = quote.GuildId,
            AddedById = quote.AddedById,
            Context = quote.Context,
            ChannelId = quote.ChannelId,
            MessageId = quote.MessageId,
            SaidAt = quote.SaidAt,
            CreatedAt = time.GetUtcNow(),
            Lines = quote.Lines,
        };
        db.Quotes.Add(saved);
        await db.SaveChangesAsync();
        return saved;
    }

    public async Task<Quote?> FindAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.GuildId == guildId && q.Id == id);
    }

    public async Task<Quote?> RandomAsync(ulong guildId, ulong? speakerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.Quotes
            .Where(q => q.GuildId == guildId && (speakerId == null || q.Lines.Any(l => l.SpeakerId == speakerId)))
            .Select(q => q.Id)
            .ToListAsync();
        if (ids.Count == 0)
            return null;

        var id = ids[Random.Shared.Next(ids.Count)];
        return await db.Quotes.Include(q => q.Lines).FirstAsync(q => q.Id == id);
    }

    public async Task<IReadOnlyList<Quote>> SearchAsync(ulong guildId, string text)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pattern = $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        return await db.Quotes
            .Include(q => q.Lines)
            .Where(q => q.GuildId == guildId && (q.Lines.Any(l => EF.Functions.ILike(l.Text, pattern)) || (q.Context != null && EF.Functions.ILike(q.Context, pattern))))
            .OrderByDescending(q => q.Id)
            .Take(15)
            .ToListAsync();
    }

    public async Task DeleteAsync(Quote quote, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Quotes.Where(q => q.Id == quote.Id).ExecuteDeleteAsync();
        db.AuditEntries.Add(new()
        {
            GuildId = quote.GuildId,
            ActorId = actorId,
            Action = "quotes.delete",
            Details = $"#{quote.Id} added by {quote.AddedById}",
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}
