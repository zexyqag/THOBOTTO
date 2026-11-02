using System.Text;
using System.Text.Json;

using THOBOTTO.Games;
using THOBOTTO.Members;

namespace THOBOTTO.DatabaseTests;

// What the panel's "your data" shows: every source's queries run on Postgres, as the page asks them.
[Collection(DatabaseCollection.Name)]
public class MemberDataTests(TestDatabase database)
{
    private const ulong Guild = 9100;
    private const ulong User = 9101;

    [Fact]
    public async Task Every_source_counts_and_writes()
    {
        var sources = typeof(IMemberData).Assembly.GetTypes()
            .Where(t => typeof(IMemberData).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IMemberData)Activator.CreateInstance(t, database)!)
            .ToList();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            await source.CountAsync(Guild, User);
            using var written = JsonDocument.Parse(await WrittenAsync(source));
        }
    }

    [Fact]
    public async Task Group_proposal_answers()
    {
        var at = new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero);
        await using (var db = database.CreateDbContext())
        {
            var game = new Game { GuildId = Guild, Name = "Valheim", RoleId = 1 };
            db.Games.Add(game);
            await db.SaveChangesAsync();
            var proposal = new GroupProposal { GuildId = Guild, GameIds = [game.Id], ProposerId = 2, ProposerName = "Sam", Times = [at, at.AddDays(1)] };
            db.GroupProposals.Add(proposal);
            await db.SaveChangesAsync();
            db.GroupProposalVotes.AddRange(
                new GroupProposalVote { ProposalId = proposal.Id, UserId = User, Choice = 1, Name = "Ana" },
                new GroupProposalVote { ProposalId = proposal.Id, UserId = 3, Choice = GroupProposals.CantMake, Name = "Bo" });
            db.GroupProposalGameVotes.Add(new GroupProposalGameVote { ProposalId = proposal.Id, UserId = User, GameId = game.Id, Name = "Ana" });
            await db.SaveChangesAsync();
        }

        var source = new GroupProposalsMemberData(database);
        Assert.Equal(2, await source.CountAsync(Guild, User));
        var written = await WrittenAsync(source);
        Assert.Contains("\"canMake\":\"2026-11-07T19:00:00+00:00\"", written, StringComparison.Ordinal);
        Assert.Contains("\"wouldPlay\":\"Valheim\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Bo", written, StringComparison.Ordinal);
    }

    private static async Task<string> WrittenAsync(IMemberData source)
    {
        using var stream = new MemoryStream();
        await using (var json = new Utf8JsonWriter(stream))
            await source.WriteAsync(Guild, User, json);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
