using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;

namespace THOBOTTO.Feedback;

public sealed class FeedbackNote
{
    public long Id { get; init; }

    public ulong? GuildId { get; init; }

    public ulong UserId { get; init; }

    public required string Name { get; init; }

    public required string Text { get; init; }

    // Where it was sent from: "discord", "activity" or "panel".
    public required string From { get; init; }

    public DateTimeOffset At { get; init; }

    public bool Done { get; set; }
}

// Feedback about the bot, from anywhere it's used: kept for the owner (the panel's Feedback page) and sent to them
// as a DM. A few a member per hour.
public sealed class FeedbackBox(IDbContextFactory<BotDbContext> dbFactory, BotOwners owners, TimeProvider time)
{
    public const int Longest = 2000;
    private const int PerHour = 5;

    private readonly ConcurrentDictionary<ulong, List<DateTimeOffset>> _sent = new();

    // The answer for who sent it.
    public async Task<string> AddAsync(ulong? guildId, string? guildName, ulong userId, string name, string text, string from)
    {
        text = text.Trim();
        if (text.Length == 0)
            return "There's nothing to send.";
        var now = time.GetUtcNow();
        var sent = _sent.GetOrAdd(userId, _ => []);
        lock (sent)
        {
            sent.RemoveAll(at => now - at > TimeSpan.FromHours(1));
            if (sent.Count >= PerHour)
                return "That's a lot of feedback at once: try again in a bit.";
            sent.Add(now);
        }
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.FeedbackNotes.Add(new() { GuildId = guildId, UserId = userId, Name = name, Text = text.Length <= Longest ? text : text[..Longest], From = from, At = now });
            await db.SaveChangesAsync();
        }
        await owners.DmAsync($"💬 Feedback from **{name}**{(guildName is null ? "" : $" in {guildName}")} ({from}, version {BotVersion.Running}):\n>>> {text}");
        return "💬 Thanks! It's been passed on.";
    }

    public async Task<IReadOnlyList<FeedbackNote>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FeedbackNotes.AsNoTracking().OrderBy(n => n.Done).ThenByDescending(n => n.At).Take(200).ToListAsync();
    }

    public async Task SetDoneAsync(long id, bool done)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.FeedbackNotes.Where(n => n.Id == id).ExecuteUpdateAsync(n => n.SetProperty(f => f.Done, done));
    }
}
