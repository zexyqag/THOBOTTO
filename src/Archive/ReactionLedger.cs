using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Archive;

// Writes reactions and poll votes for the Archiver's writer: a message's current rows are read, the change
// decided over them, and written. Only the writer calls this, so nothing else races it.
public sealed class ReactionLedger(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    internal async Task ApplyAsync(Work work, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        switch (work)
        {
            case ReactionAdded added:
                if (ReactionChanges.Add(await CurrentReactionsAsync(db, added.MessageId, ct), added.MessageId, added.Reaction, now) is { } row)
                    db.ArchivedReactions.Add(row);
                break;
            case ReactionRemoved removed:
                if (ReactionChanges.Remove(await CurrentReactionsAsync(db, removed.MessageId, ct), removed.Reaction) is { } ended)
                    ended.RemovedAt = now;
                break;
            case ReactionsRemovedAll all:
                foreach (var reaction in await CurrentReactionsAsync(db, all.MessageId, ct))
                    reaction.RemovedAt = now;
                break;
            case ReactionsRemovedEmoji emoji:
                foreach (var reaction in ReactionChanges.RemoveEmoji(await CurrentReactionsAsync(db, emoji.MessageId, ct), emoji.EmojiId, emoji.EmojiName))
                    reaction.RemovedAt = now;
                break;
            case ReactionsFetched fetched:
                var (addedReactions, goneReactions) = ReactionChanges.Reconcile(await CurrentReactionsAsync(db, fetched.MessageId, ct), fetched.Reactions, fetched.MessageId, now);
                db.ArchivedReactions.AddRange(addedReactions);
                foreach (var reaction in goneReactions)
                    reaction.RemovedAt = now;
                break;
            case VoteAdded vote:
                if (VoteChanges.Add(await CurrentVotesAsync(db, vote.MessageId, ct), vote.MessageId, vote.Vote, now) is { } cast)
                    db.ArchivedPollVotes.Add(cast);
                break;
            case VoteRemoved vote:
                if (VoteChanges.Remove(await CurrentVotesAsync(db, vote.MessageId, ct), vote.Vote) is { } withdrawn)
                    withdrawn.RemovedAt = now;
                break;
            case VotesFetched fetched:
                var (addedVotes, goneVotes) = VoteChanges.Reconcile(await CurrentVotesAsync(db, fetched.MessageId, ct), fetched.Votes, fetched.MessageId, now);
                db.ArchivedPollVotes.AddRange(addedVotes);
                foreach (var vote in goneVotes)
                    vote.RemovedAt = now;
                break;
        }
        await db.SaveChangesAsync(ct);
    }

    private static Task<List<ArchivedReaction>> CurrentReactionsAsync(BotDbContext db, ulong messageId, CancellationToken ct)
        => db.ArchivedReactions.Where(r => r.MessageId == messageId && r.RemovedAt == null).ToListAsync(ct);

    private static Task<List<ArchivedPollVote>> CurrentVotesAsync(BotDbContext db, ulong messageId, CancellationToken ct)
        => db.ArchivedPollVotes.Where(v => v.MessageId == messageId && v.RemovedAt == null).ToListAsync(ct);
}
