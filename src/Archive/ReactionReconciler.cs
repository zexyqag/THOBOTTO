using System.Net;

using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Archive;

// Asks Discord who reacted to, and who voted on, the messages whose fetched counts don't add up to their rows,
// and hands the answer to the Archiver. Paced: a backfill can queue thousands, each at least one request.
public sealed class ReactionReconciler(RestClient rest, Archiver archiver, ILogger<ReactionReconciler> logger) : BackgroundService
{
    private const int PageSize = 100;

    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var check in archiver.ChecksAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(check, stoppingToken);
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // The message is gone, or its channel can't be read.
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Asking who reacted to message {MessageId} in {GuildId} failed", check.MessageId, check.GuildId);
            }
            await Task.Delay(Pace, stoppingToken);
        }
    }

    // All or nothing per message: a half-fetched list would end reactions that are still there.
    private async Task CheckAsync(ReactionCheck check, CancellationToken ct)
    {
        if (check.Reactions.Count > 0)
        {
            var seen = new List<SeenReaction>();
            foreach (var count in check.Reactions)
            {
                var emoji = count.EmojiId is { } id ? new ReactionEmojiProperties(count.EmojiName, id) : new ReactionEmojiProperties(count.EmojiName);
                if (count.Normal > 0)
                    await AddUsersAsync(seen, check, emoji, count, ReactionType.Normal, ct);
                if (count.Burst > 0)
                    await AddUsersAsync(seen, check, emoji, count, ReactionType.Burst, ct);
            }
            archiver.OnReactionsFetched(check.GuildId, check.ChannelId, check.MessageId, seen);
        }

        if (check.AnswerIds.Count > 0)
        {
            var votes = new List<SeenVote>();
            foreach (var answerId in check.AnswerIds)
            {
                await foreach (var user in rest.GetMessagePollAnswerVotersAsync(check.ChannelId, check.MessageId, answerId, new() { BatchSize = PageSize }).WithCancellation(ct))
                    votes.Add(new(user.Id, answerId));
            }
            archiver.OnVotesFetched(check.GuildId, check.ChannelId, check.MessageId, votes);
        }
    }

    private async Task AddUsersAsync(List<SeenReaction> seen, ReactionCheck check, ReactionEmojiProperties emoji, ReactionCount count, ReactionType type, CancellationToken ct)
    {
        var page = new MessageReactionsPaginationProperties { Type = type, BatchSize = PageSize };
        await foreach (var user in rest.GetMessageReactionsAsync(check.ChannelId, check.MessageId, emoji, page).WithCancellation(ct))
            seen.Add(new(user.Id, count.EmojiId, count.EmojiName, type == ReactionType.Burst));
    }
}
