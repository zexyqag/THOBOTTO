using Microsoft.Extensions.Logging.Abstractions;

using THOBOTTO.Discord;

namespace THOBOTTO.DatabaseTests;

// Whom reactions to a message the bot posted for someone count for.
[Collection(DatabaseCollection.Name)]
public class ReactionCreditTests(TestDatabase database)
{
    [Fact]
    public async Task The_member_it_was_posted_for_unless_anonymous_or_not_counted_here()
    {
        await using (var db = database.CreateDbContext())
        {
            db.MemberPosts.AddRange(
                new() { MessageId = 9101, GuildId = 91, ChannelId = 1, UserId = 501, PostedAt = DateTimeOffset.UnixEpoch },
                new() { MessageId = 9102, GuildId = 91, ChannelId = 1, UserId = 502, Anonymous = true, PostedAt = DateTimeOffset.UnixEpoch },
                new() { MessageId = 9103, GuildId = 91, ChannelId = 1, UserId = 503, Credited = false, PostedAt = DateTimeOffset.UnixEpoch });
            await db.SaveChangesAsync();
        }
        var posts = new AsMember(null!, null!, database, TimeProvider.System, NullLogger<AsMember>.Instance);

        Assert.Equal(501UL, await posts.CreditedAsync(91, 9101, null));
        Assert.Null(await posts.CreditedAsync(91, 9102, null));
        Assert.Null(await posts.CreditedAsync(91, 9103, null));
        Assert.Null(await posts.CreditedAsync(91, 9104, null));
    }
}
