using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Events;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;

namespace THOBOTTO.Games;

public sealed record ProposalView(string Content, IReadOnlyList<ActionRowProperties> Rows);

// A planned proposal's session as the server has it now; State null when it's gone. Suggestions: other times
// suggested for it, and how one moves it.
public sealed record PlannedSession(string Title, string Server, string? State, DateTimeOffset At, string? Link,
    IReadOnlyList<string> In, IReadOnlyList<string> Waiting, IReadOnlyList<string> Outside,
    IReadOnlyList<PlannedSuggestion> Suggestions, string MoveRule, bool BeforeStart);

public sealed record PlannedSuggestion(long Id, DateTimeOffset At, string By, IReadOnlyList<string> WorksFor);

// Sessions proposed in a group DM by someone who installed the bot to their own account. The group votes there with
// buttons, on the time and, when several, the game; the bot isn't in the group, so it can't post there or read it, and
// the message changes only when someone presses. The proposer plans the favourites as a session in the server, which
// signs up the voters who are members; the group can still sign up for it, or see how it stands, from there.
public sealed class GroupProposals(
    IDbContextFactory<BotDbContext> dbFactory,
    GatewayClient gateway,
    GameDirectory games,
    GameSessions sessions,
    EventBoard board,
    PanelAccess members,
    ModuleState modules,
    SettingsStore settings,
    TimeZones zones,
    RestClient rest,
    TimeProvider time,
    ILogger<GroupProposals> logger)
{
    public const int CantMake = -1;
    public const int MaxGames = 5;
    public const string NoGames = "None of the servers you're in with THOBOTTO has games set up: sessions are planned there, in one of its games.";

    // How long after its last time a proposal is kept, to answer a late press.
    private static readonly TimeSpan Kept = TimeSpan.FromDays(7);

    // How long a proposal's vote is open, unless it's settled sooner.
    private static readonly TimeSpan Open = TimeSpan.FromHours(24);

    // The games of the servers the person shares with the bot, where games are on.
    public async Task<IReadOnlyList<(Guild Guild, Game Game)>> GamesForAsync(ulong userId)
    {
        var found = new List<(Guild, Game)>();
        foreach (var guild in await members.GuildsAsync(userId))
        {
            if (await modules.IsEnabledAsync(guild.Id, GameDirectory.ModuleId))
                found.AddRange((await games.ListAsync(guild.Id)).Select(g => (guild, g)));
        }
        return found;
    }

    public static string Choice(Guild guild, Game game) => $"{guild.Id}:{game.Id}";

    // The games chosen ("<server>:<game>", all of one server), the times, and a title (else the planned game's usual).
    // A game's names in a group DM: GamePicks' and with its server, and "<server>:<game>" (what forms send).
    public static IEnumerable<string> Names((Guild Guild, Game Game) game)
        => [.. GamePicks.Names(game.Game), $"{GameDirectory.Label(game.Game)} · {game.Guild.Name}", $"{game.Game.Name} · {game.Guild.Name}", Choice(game.Guild, game.Game)];

    // The games named (several separated by commas, all of one server), the times, and a title (else the planned game's
    // usual); how it's settled, how many that takes, and how a suggested time moves the session it becomes.
    public async Task<(ProposalView? View, string? Problem)> ProposeAsync(User user, string gameNames, string when, string? title, string? decide = null, int? enough = null, string? move = null)
    {
        var candidates = await GamesForAsync(user.Id);
        if (candidates.Count == 0)
            return (null, NoGames);
        var (named, unknown) = GamePicks.Parse(gameNames, candidates, Names);
        if (unknown is not null)
            return (null, unknown);
        var picked = named.Select(n => (guildId: n.Guild.Id, gameId: n.Game.Id)).ToList();
        if (picked.Select(p => p.guildId).Distinct().Count() > 1)
            return (null, "Pick games of one server: the session is planned there.");
        var gameIds = picked.Select(p => p.gameId).Distinct().ToList();
        if (gameIds.Count > MaxGames)
            return (null, $"At most {MaxGames} games.");
        if (await members.InAsync(picked[0].guildId, user.Id) is not var (guild, member))
            return (null, "Pick one of the listed games.");
        var rules = await settings.GetAsync<GameRules>(guild.Id, GameDirectory.ModuleId);
        var found = new List<Game>();
        foreach (var gameId in gameIds)
        {
            if (await games.FindAsync(guild.Id, gameId) is not { } game)
                return (null, "Pick one of the listed games.");
            if (await sessions.HomeAsync(game, rules) is null)
                return (null, GameSessions.NoChannel(game));
            if (await sessions.RefusalAsync(guild, member, gameId) is { } refusal)
                return (null, refusal);
            found.Add(game);
        }

        var (zone, own) = await zones.ForAsync(guild.Id, user.Id);
        var now = Instant.FromDateTimeOffset(time.GetUtcNow());
        var times = new List<DateTimeOffset>();
        foreach (var text in when.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = WhenParser.Parse(text, zone, now);
            if (parsed.At is not { } at)
                return (null, $"`{text}`: {parsed.Problem}\n{TimeZones.Hint(zone, own)}");
            if (at <= now)
                return (null, $"`{text}` has already passed.");
            times.Add(at.ToDateTimeOffset());
        }
        if (times.Count == 0)
            return (null, "Give a time.");
        if (times.Count > EventBoard.MaxTimeOptions)
            return (null, $"At most {EventBoard.MaxTimeOptions} times.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await ForgetOldAsync(db);
        var proposal = new GroupProposal
        {
            GuildId = guild.Id,
            GameIds = [.. gameIds],
            ProposerId = user.Id,
            ProposerName = NameOf(user),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Times = [.. times.Distinct().Order()],
            Decide = decide ?? rules.SessionDecide,
            Enough = enough,
            Move = move,
            ClosesAt = time.GetUtcNow() + Open,
            CreatedAt = time.GetUtcNow(),
        };
        db.GroupProposals.Add(proposal);
        await db.SaveChangesAsync();
        return (View(proposal, [], [], guild.Name, found, await EnoughAsync(proposal, found, [])), null);
    }

    // A time pressed again is taken back; Can't takes back every time and game, and a time takes back Can't.
    public async Task<(ProposalView? View, string? Problem)> VoteAsync(long proposalId, User user, int choice)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        if (proposal.EventId is null)
        {
            if (choice < CantMake || choice >= proposal.Times.Length)
                return (null, "That isn't one of its times.");
            var mine = await db.GroupProposalVotes.Where(v => v.ProposalId == proposalId && v.UserId == user.Id).ToListAsync();
            if (mine.FirstOrDefault(v => v.Choice == choice) is { } again)
                db.GroupProposalVotes.Remove(again);
            else
            {
                db.GroupProposalVotes.RemoveRange(mine.Where(v => choice == CantMake || v.Choice == CantMake));
                db.GroupProposalVotes.Add(new() { ProposalId = proposalId, UserId = user.Id, Choice = choice, Name = NameOf(user) });
                if (choice == CantMake)
                    await db.GroupProposalGameVotes.Where(v => v.ProposalId == proposalId && v.UserId == user.Id).ExecuteDeleteAsync();
            }
            await RenameAsync(db, proposalId, user);
            await db.SaveChangesAsync();
            await SettleWhenDueAsync(db, proposal, user.Id);
        }
        return (await ViewAsync(db, proposal), null);
    }

    // A time someone suggests, with their vote; it can settle the proposal at once.
    public async Task<(ProposalView? View, string? Problem)> SuggestAsync(long proposalId, User user, string when)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        if (proposal.EventId is not null || proposal.Closed)
            return (await ViewAsync(db, proposal), null);
        if (proposal.Times.Length >= EventBoard.MaxTimeOptions)
            return (null, $"It already has {EventBoard.MaxTimeOptions} times.");
        var (zone, own) = await zones.ForAsync(proposal.GuildId, user.Id);
        var parsed = WhenParser.Parse(when.Trim(), zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        if (parsed.At is not { } at)
            return (null, $"`{when.Trim()}`: {parsed.Problem}\n{TimeZones.Hint(zone, own)}");
        var startsAt = at.ToDateTimeOffset();
        if (startsAt <= time.GetUtcNow())
            return (null, "That time has already passed.");
        if (proposal.Times.Contains(startsAt))
            return (null, "That's already one of its times: press it.");

        proposal.Times = [.. proposal.Times, startsAt];
        await db.GroupProposalVotes.Where(v => v.ProposalId == proposalId && v.UserId == user.Id && v.Choice == CantMake).ExecuteDeleteAsync();
        db.GroupProposalVotes.Add(new() { ProposalId = proposalId, UserId = user.Id, Choice = proposal.Times.Length - 1, Name = NameOf(user) });
        await db.SaveChangesAsync();
        await SettleWhenDueAsync(db, proposal, user.Id);
        return (await ViewAsync(db, proposal), null);
    }

    // A game someone would play, pressed again to take it back; it takes back Can't.
    public async Task<(ProposalView? View, string? Problem)> PickAsync(long proposalId, User user, long gameId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        if (proposal.EventId is null)
        {
            if (!proposal.GameIds.Contains(gameId))
                return (null, "That isn't one of its games.");
            if (await db.GroupProposalGameVotes.FindAsync(proposalId, user.Id, gameId) is { } again)
                db.GroupProposalGameVotes.Remove(again);
            else
            {
                db.GroupProposalGameVotes.Add(new() { ProposalId = proposalId, UserId = user.Id, GameId = gameId, Name = NameOf(user) });
                await db.GroupProposalVotes.Where(v => v.ProposalId == proposalId && v.UserId == user.Id && v.Choice == CantMake).ExecuteDeleteAsync();
            }
            await RenameAsync(db, proposalId, user);
            await db.SaveChangesAsync();
        }
        return (await ViewAsync(db, proposal), null);
    }

    // The proposer's, whatever the rule: the time most can make (the earliest of a tie) is planned now.
    public async Task<(ProposalView? View, string? Problem)> PlanAsync(long proposalId, User user)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        if (proposal.EventId is not null || proposal.Closed)
            return (await ViewAsync(db, proposal), null);
        if (proposal.ProposerId != user.Id)
            return (null, $"Only {proposal.ProposerName} can plan it, having proposed it.");

        var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposalId).ToListAsync();
        if (Favourite(proposal.Times, votes, time.GetUtcNow()) is not { } chosen)
            return (null, "All its times have passed: propose new ones.");
        return await PlanCoreAsync(db, proposal, chosen) is { } problem ? (null, problem) : (await ViewAsync(db, proposal), null);
    }

    // As its rule says, after a press: planned at once when it's settled, and the proposer told when it wasn't them.
    private async Task SettleWhenDueAsync(BotDbContext db, GroupProposal proposal, ulong byUserId)
    {
        if (proposal.EventId is not null || proposal.Closed)
            return;
        var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).ToListAsync();
        var picks = await db.GroupProposalGameVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).ToListAsync();
        var now = time.GetUtcNow();
        var enough = await EnoughAsync(proposal, await GamesOfAsync(proposal), picks);
        if (Deciding.Settled(proposal.Decide, proposal.Times, Counts(proposal, votes), enough, now, closed: now >= proposal.ClosesAt) is { } chosen
            && await PlanCoreAsync(db, proposal, chosen) is null && byUserId != proposal.ProposerId)
            await TellProposerAsync(proposal);
    }

    // Those whose vote has closed (or whose times have all passed) are settled by their rule, or closed unplanned.
    public async Task SweepAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        foreach (var proposal in await db.GroupProposals.Where(p => p.EventId == null && !p.Closed).ToListAsync())
        {
            var lapsed = Deciding.Lapsed(proposal.Times, now);
            if (!lapsed && (proposal.Decide == Deciding.PlannerDecides || proposal.ClosesAt > now))
                continue;
            var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).ToListAsync();
            var picks = await db.GroupProposalGameVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).ToListAsync();
            var enough = await EnoughAsync(proposal, await GamesOfAsync(proposal), picks);
            var reason = "nobody could make any of its times still ahead";
            if (!lapsed && Deciding.Settled(proposal.Decide, proposal.Times, Counts(proposal, votes), enough, now, closed: true) is { } chosen)
            {
                if (await PlanCoreAsync(db, proposal, chosen) is not { } problem)
                {
                    await TellProposerAsync(proposal);
                    continue;
                }
                reason = problem;
            }
            proposal.Closed = true;
            await db.SaveChangesAsync();
            logger.LogInformation("Group proposal {ProposalId} of {GuildId} closed unplanned: {Reason}", proposal.Id, proposal.GuildId, reason);
        }
    }

    // That time becomes a session in the game most would play (the first of a tie), planned by the proposer, who must
    // still be in the server; the voters who are members are signed up. Null, or why not.
    private async Task<string?> PlanCoreAsync(BotDbContext db, GroupProposal proposal, int chosen)
    {
        if (await members.InAsync(proposal.GuildId, proposal.ProposerId) is not var (guild, member))
            return $"{proposal.ProposerName} is no longer in that server, and it's planned there in their name.";
        var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).ToListAsync();
        var picks = await db.GroupProposalGameVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).ToListAsync();
        if (FavouriteGame([.. (await GamesOfAsync(proposal)).Select(g => g.Id)], picks) is not { } gameId)
            return "Its games are gone from the server: propose others.";
        var (e, problem) = await sessions.CreateAsync(guild, member, gameId, [proposal.Times[chosen]], proposal.Title, voice: null,
            pingRole: true, poll: false, modeId: null, players: null, channelId: 0, move: proposal.Move);
        if (e is null)
            return problem;

        (proposal.EventId, proposal.Planned) = (e.Id, chosen);
        await db.SaveChangesAsync();
        foreach (var vote in votes.Where(v => v.Choice == chosen))
        {
            if (await members.InAsync(proposal.GuildId, vote.UserId) is not null)
                await board.RsvpAsync(e.Id, vote.UserId, RsvpStatuses.In);
        }
        return null;
    }

    // The bot can't post in the group: the proposer hears of it by DM, and the group on its next press.
    private async Task TellProposerAsync(GroupProposal proposal)
    {
        var e = proposal.EventId is { } id ? await board.FindAsync(proposal.GuildId, id) : null;
        if (e is null)
            return;
        var link = e.MessageId is { } message ? $" · [the session]({Notifier.Link(e.GuildId, e.ChannelId, message)})" : "";
        try
        {
            var dm = await rest.GetDMChannelAsync(proposal.ProposerId);
            await dm.SendMessageAsync(new() { Content = $"**{e.Title}**, which you proposed in a group DM, is planned in **{ServerName(proposal.GuildId)}**: <t:{e.StartsAt!.Value.ToUnixTimeSeconds()}:F>{link}" });
        }
        catch (RestException ex)
        {
            logger.LogDebug("Telling {UserId} their group proposal {ProposalId} was planned failed: {Message}", proposal.ProposerId, proposal.Id, ex.Message);
        }
    }

    private static int[] Counts(GroupProposal proposal, IReadOnlyList<GroupProposalVote> votes)
        => [.. Enumerable.Range(0, proposal.Times.Length).Select(i => votes.Count(v => v.Choice == i))];

    // Its own number, else the player count of the game most would play, else the server's number.
    private async Task<int> EnoughAsync(GroupProposal proposal, IReadOnlyList<Game> found, IReadOnlyList<GroupProposalGameVote> picks)
        => proposal.Enough
            ?? (FavouriteGame([.. found.Select(g => g.Id)], picks) is { } gameId ? found.First(g => g.Id == gameId).Players : null)
            ?? (await settings.GetAsync<EventRules>(proposal.GuildId, EventBoard.ModuleId)).EnoughPeople;

    // Once planned: signing up for the session (or taking it back) from the group, for members; or just how it stands.
    // Said: the server's answer, or why not when there's no view.
    public async Task<(ProposalView? View, string? Said)> GoAsync(long proposalId, User user, string answer)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        string? said = null;
        if (proposal.EventId is { } eventId && answer is "in" or "out")
        {
            if (await members.InAsync(proposal.GuildId, user.Id) is null)
                return (null, $"You're not in {ServerName(proposal.GuildId)}: ask someone there for an invite.");
            said = await board.RsvpAsync(eventId, user.Id, answer == "in" ? RsvpStatuses.In : RsvpStatuses.Out);
        }
        return (await ViewAsync(db, proposal), said);
    }

    // Once planned, from the group: another time suggested, saying one works, or (its planner, or someone with
    // events.manage) moving it there. Members only. Said: the server's answer, or why not when there's no view.
    public async Task<(ProposalView? View, string? Said)> SuggestMoveAsync(long proposalId, User user, string when)
        => await AsMemberAsync(proposalId, user, async (proposal, eventId, _) =>
        {
            var (zone, own) = await zones.ForAsync(proposal.GuildId, user.Id);
            var parsed = WhenParser.Parse(when.Trim(), zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
            return parsed.At is { } at ? await board.SuggestAsync(eventId, user.Id, at.ToDateTimeOffset()) : $"`{when.Trim()}`: {parsed.Problem}\n{TimeZones.Hint(zone, own)}";
        });

    public async Task<(ProposalView? View, string? Said)> WorksForAsync(long proposalId, User user, long suggestionId)
        => await AsMemberAsync(proposalId, user, (_, _, _) => board.WorksForAsync(suggestionId, user.Id));

    public async Task<(ProposalView? View, string? Said)> MoveToAsync(long proposalId, User user, long suggestionId)
        => await AsMemberAsync(proposalId, user, async (proposal, eventId, member) =>
        {
            if (await board.SuggestionAsync(suggestionId) is not { } suggestion || suggestion.EventId != eventId || await board.FindAsync(proposal.GuildId, eventId) is not { } e)
                return "That suggestion is gone.";
            return await EventMoving.NotTheirsAsync(e, member.Guild, member.Member, members.Control) ?? await board.MoveAsync(eventId, suggestion.StartsAt);
        });

    private async Task<(ProposalView? View, string? Said)> AsMemberAsync(long proposalId, User user, Func<GroupProposal, long, (Guild Guild, GuildUser Member), Task<string>> act)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        if (proposal.EventId is not { } eventId)
            return (await ViewAsync(db, proposal), null);
        if (await members.InAsync(proposal.GuildId, user.Id) is not { } member)
            return (null, $"You're not in {ServerName(proposal.GuildId)}: ask someone there for an invite.");
        var said = await act(proposal, eventId, member);
        return (await ViewAsync(db, proposal), said);
    }

    public static int? Favourite(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<GroupProposalVote> votes, DateTimeOffset now)
        => Enumerable.Range(0, times.Count).Where(i => times[i] > now)
            .OrderByDescending(i => votes.Count(v => v.Choice == i)).ThenBy(i => times[i])
            .Select(i => (int?)i).FirstOrDefault();

    public static long? FavouriteGame(IReadOnlyList<long> gameIds, IReadOnlyList<GroupProposalGameVote> picks)
        => Deciding.Favourite([.. gameIds.Select(id => picks.Count(p => p.GameId == id))]) is { } i ? gameIds[i] : null;

    public static InteractionCallbackProperties Posted((ProposalView? View, string? Problem) result)
        => InteractionCallback.Message(result.View is { } view
            ? new() { Content = view.Content, Components = view.Rows, AllowedMentions = AllowedMentionsProperties.None }
            : Replies.Ephemeral(result.Problem!));

    private static async Task RenameAsync(BotDbContext db, long proposalId, User user)
    {
        await db.GroupProposalVotes.Where(v => v.ProposalId == proposalId && v.UserId == user.Id).ExecuteUpdateAsync(v => v.SetProperty(x => x.Name, NameOf(user)));
        await db.GroupProposalGameVotes.Where(v => v.ProposalId == proposalId && v.UserId == user.Id).ExecuteUpdateAsync(v => v.SetProperty(x => x.Name, NameOf(user)));
    }

    // Its games still in the server, in the order given.
    private async Task<IReadOnlyList<Game>> GamesOfAsync(GroupProposal proposal)
    {
        var found = new List<Game>();
        foreach (var id in proposal.GameIds)
        {
            if (await games.FindAsync(proposal.GuildId, id) is { } game)
                found.Add(game);
        }
        return found;
    }

    private string ServerName(ulong guildId) => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.Name : "the server";

    private async Task<ProposalView> ViewAsync(BotDbContext db, GroupProposal proposal)
    {
        var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).OrderBy(v => v.Name).ToListAsync();
        var server = ServerName(proposal.GuildId);
        var found = await GamesOfAsync(proposal);
        if (proposal.Closed)
            return new($"**{proposal.Title ?? (found is [var only] ? GameSessions.DefaultTitle(only, null) : "The session")}** in **{server}** wasn't planned: nobody could make any of its times still ahead, or its proposer has left the server.", []);
        if (proposal is not { EventId: { } eventId, Planned: { } planned })
        {
            var picks = await db.GroupProposalGameVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).OrderBy(v => v.Name).ToListAsync();
            return View(proposal, votes, picks, server, found, await EnoughAsync(proposal, found, picks));
        }

        var e = await board.FindAsync(proposal.GuildId, eventId);
        var outside = new List<string>();
        foreach (var vote in votes.Where(v => v.Choice == planned))
        {
            if (await members.InAsync(proposal.GuildId, vote.UserId) is null)
                outside.Add(vote.Name);
        }
        var going = e is null ? [] : await db.EventRsvps.AsNoTracking()
            .Where(r => r.EventId == e.Id && (r.Status == RsvpStatuses.In || r.Status == RsvpStatuses.Waiting)).OrderBy(r => r.At).ToListAsync();
        gateway.Cache.Guilds.TryGetValue(proposal.GuildId, out var guild);
        string Name(ulong id) => guild is not null && guild.Users.TryGetValue(id, out var user) ? NameOf(user) : votes.FirstOrDefault(v => v.UserId == id)?.Name ?? "someone";
        var suggested = e is null ? [] : await board.SuggestionsAsync(e.Id);
        var inCount = going.Count(r => r.Status == RsvpStatuses.In);
        var moveRule = (await settings.GetAsync<EventRules>(proposal.GuildId, EventBoard.ModuleId)).Move == Moving.Majority
            ? $"It moves when {Moving.Needed(inCount)} of the {inCount} who are in say it works."
            : $"{Name(e?.CreatorId ?? proposal.ProposerId)} moves it.";
        return Planned(proposal.Id, new(
            e?.Title ?? proposal.Title ?? (found is [var game, ..] ? GameSessions.DefaultTitle(game, null) : "The session"),
            server,
            e?.State,
            e?.StartsAt ?? proposal.Times[planned],
            e is { MessageId: { } message } ? Notifier.Link(e.GuildId, e.ChannelId, message) : null,
            [.. going.Where(r => r.Status == RsvpStatuses.In).Select(r => Name(r.UserId))],
            [.. going.Where(r => r.Status == RsvpStatuses.Waiting).Select(r => Name(r.UserId))],
            outside,
            [.. suggested.Select(x => new PlannedSuggestion(x.Suggestion.Id, x.Suggestion.StartsAt, Name(x.Suggestion.ById), [.. x.WorksFor.Select(Name)]))],
            moveRule,
            (e?.StartsAt ?? proposal.Times[planned]) > time.GetUtcNow()));
    }

    public static ProposalView View(GroupProposal proposal, IReadOnlyList<GroupProposalVote> votes, IReadOnlyList<GroupProposalGameVote> picks, string server, IReadOnlyList<Game> found, int enough)
    {
        var several = proposal.Times.Length > 1;
        var choosing = found.Count > 1;
        var title = proposal.Title is { } own
            ? $"**{own}**{(found.Count == 0 ? "" : $" ({string.Join(" or ", found.Select(GameDirectory.Label))})")}"
            : $"**{(found.Count == 0 ? "A session" : $"{string.Join(" or ", found.Select(g => g.Name))} session")}**";
        var lines = new List<string> { $"📅 {title} in **{server}**, proposed by {proposal.ProposerName}" };
        for (var i = 0; i < proposal.Times.Length; i++)
            lines.Add($"{(several ? Keycap(i) + " " : "")}{When(proposal.Times[i])}: {Who(votes.Where(v => v.Choice == i).Select(v => v.Name))}");
        if (choosing)
            lines.AddRange(found.Select(g => $"{GameDirectory.Label(g)}: {Who(picks.Where(p => p.GameId == g.Id).Select(p => p.Name))}"));
        if (votes.Where(v => v.Choice == CantMake).Select(v => v.Name).ToList() is { Count: > 0 } cant)
            lines.Add($"Can't: {string.Join(", ", cant)}");
        var closes = $"<t:{proposal.ClosesAt.ToUnixTimeSeconds()}:R>";
        var settled = proposal.Decide switch
        {
            Deciding.PlannerDecides => $"{proposal.ProposerName} plans it in the server with Plan it.",
            Deciding.AsSoonAsEnough => $"It's planned in the server as soon as a time has {enough}, else by the most votes {closes}; {proposal.ProposerName} can plan it sooner.",
            _ => $"It's planned in the server by the most votes {closes}; {proposal.ProposerName} can plan it sooner.",
        };
        lines.Add($"-# {(several ? "Press the times you can make" : "Press I'm in if you can make it")}{(choosing ? " and the games you'd play" : "")} (again to take it back), or suggest another. {settled}");

        var times = several
            ? Enumerable.Range(0, proposal.Times.Length).Select(i => new ButtonProperties($"groupvote:{proposal.Id}:{i}", EmojiProperties.Standard(GameDirectory.ButtonEmoji(Keycap(i))!), ButtonStyle.Secondary)).ToList()
            : [new ButtonProperties($"groupvote:{proposal.Id}:0", "I'm in", ButtonStyle.Success)];
        List<ButtonProperties> end = [new($"groupvote:{proposal.Id}:{CantMake}", "Can't", ButtonStyle.Secondary)];
        if (proposal.Times.Length < EventBoard.MaxTimeOptions)
            end.Add(new($"groupsuggest:{proposal.Id}", "Suggest a time", ButtonStyle.Secondary));
        end.Add(new($"groupplan:{proposal.Id}", "Plan it", ButtonStyle.Primary));
        var rows = choosing
            ? times.Chunk(5).Concat(found.Select(GameButton).Chunk(5)).Append([.. end])
            : times.Concat(end).Chunk(5);
        return new(string.Join('\n', lines), [.. rows.Select(row => new ActionRowProperties(row))]);

        ButtonProperties GameButton(Game game)
            => new($"groupgame:{proposal.Id}:{game.Id}", game.Name.Length <= 80 ? game.Name : game.Name[..80], ButtonStyle.Secondary)
            {
                Emoji = GameDirectory.ButtonEmoji(game.Emoji) is { } emoji ? EmojiProperties.Standard(emoji) : null,
            };
    }

    public static ProposalView Planned(long proposalId, PlannedSession session)
    {
        var (title, server) = ($"**{session.Title}**", $"**{session.Server}**");
        switch (session.State)
        {
            case null:
                return new($"{title} is no longer planned in {server}.", []);
            case EventStates.Cancelled:
                return new($"❌ {title} in {server} was cancelled.", []);
            case EventStates.Over:
                return new($"🏁 {title} in {server} is over: it was {When(session.At)}.", []);
        }
        var lines = new List<string>
        {
            $"✅ {title} is planned in {server}: {When(session.At)}{(session.Link is { } link ? $" · [the session]({link})" : "")}",
            $"In: {Who(session.In)}",
        };
        if (session.Waiting.Count > 0)
            lines.Add($"Waiting for a spot: {string.Join(", ", session.Waiting)}");
        if (session.Outside.Count > 0)
            lines.Add($"Not in the server: {string.Join(", ", session.Outside)}");
        for (var i = 0; i < session.Suggestions.Count; i++)
        {
            var x = session.Suggestions[i];
            lines.Add($"💡 **{i + 1}.** {When(x.At)}, suggested by {x.By} · works for {Who(x.WorksFor)}");
        }
        if (session.Suggestions.Count > 0)
            lines.Add($"-# {session.MoveRule}");
        lines.Add($"-# I'm in signs you up there (if you're in {session.Server}), Can't takes it back. Refresh shows what changed there.");
        List<ActionRowProperties> rows =
        [
            new(
            [
                new ButtonProperties($"groupgo:{proposalId}:in", "I'm in", ButtonStyle.Success),
                new ButtonProperties($"groupgo:{proposalId}:out", "Can't", ButtonStyle.Secondary),
                new ButtonProperties($"groupgo:{proposalId}:refresh", "Refresh", ButtonStyle.Secondary),
            ]),
        ];
        if (session.State == EventStates.Scheduled && session.BeforeStart)
        {
            if (session.Suggestions.Count < EventBoard.MaxSuggestions)
                rows.Add(new([new ButtonProperties($"groupsuggestmove:{proposalId}", "Suggest another time", EmojiProperties.Standard("💡"), ButtonStyle.Secondary)]));
            rows.AddRange(session.Suggestions.Select((x, i) => new ActionRowProperties(
            [
                new ButtonProperties($"groupworks:{proposalId}:{x.Id}", $"{i + 1} works for me", EmojiProperties.Standard("👍"), ButtonStyle.Secondary),
                new ButtonProperties($"groupmoveto:{proposalId}:{x.Id}", $"Move to {i + 1}", ButtonStyle.Secondary),
            ])));
        }
        return new(string.Join('\n', lines), rows);
    }

    private static string Who(IEnumerable<string> names) => names.ToList() is { Count: > 0 } list ? string.Join(", ", list) : "nobody yet";

    private static string When(DateTimeOffset at) => $"<t:{at.ToUnixTimeSeconds()}:F> (<t:{at.ToUnixTimeSeconds()}:R>)";

    private static string Keycap(int i) => i < 9 ? $"{i + 1}\uFE0F\u20E3" : "\U0001F51F";

    private static string NameOf(User user) => user.GlobalName ?? user.Username;

    private async Task ForgetOldAsync(BotDbContext db)
    {
        var cutoff = time.GetUtcNow() - Kept;
        var old = await db.GroupProposals.Where(p => p.CreatedAt < cutoff).Select(p => new { p.Id, p.Times }).ToListAsync();
        var gone = old.Where(p => p.Times.Max() < cutoff).Select(p => p.Id).ToList();
        if (gone.Count > 0)
            await db.GroupProposals.Where(p => gone.Contains(p.Id)).ExecuteDeleteAsync();
    }
}

public sealed class ProposeCommand(GroupProposals proposals) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("propose", "Propose a game session to this group, to plan in your server",
        IntegrationTypes = [ApplicationIntegrationType.UserInstall], Contexts = [InteractionContextType.DMChannel])]
    public async Task<InteractionCallbackProperties> ProposeAsync(
        [SlashCommandParameter(Description = "The game, or several to vote on (of one server), separated by commas", MaxLength = 300, AutocompleteProviderType = typeof(GroupGameAutocomplete))] string games,
        [SlashCommandParameter(Description = "When, in your time: now, fri 8pm, tomorrow 19:30; several with commas to vote on", MaxLength = 400)] string when,
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null,
        [SlashCommandParameter(Name = "settled-by", Description = "How it's settled (default: the server's setting for sessions)")] Settling? settledBy = null,
        [SlashCommandParameter(Name = "people-needed", Description = "With \"as soon as enough\": how many (default: the game's player count, else the server's number)", MinValue = 1, MaxValue = 100)] int? peopleNeeded = null,
        [SlashCommandParameter(Name = "moves-when", Description = "Once planned, when a suggested time moves it (default: the server's setting)")] Moves? movesWhen = null)
        => GroupProposals.Posted(await proposals.ProposeAsync(Context.User, games, when, title,
            settledBy is { } settled ? Deciding.Of(settled) : null, peopleNeeded, movesWhen is { } moves ? Moving.Of(moves) : null));
}

// The games of every server the person shares with the bot, several separated by commas, named with their server when
// there are several; once one is picked, those of its server.
public sealed class GroupGameAutocomplete(GroupProposals proposals) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var text = option.Value ?? "";
        var found = await proposals.GamesForAsync(context.User.Id);
        var before = GamePicks.Split(text).SkipLast(text.TrimEnd().EndsWith(',') ? 0 : 1).ToList();
        var server = found.FirstOrDefault(f => before.Any(b => GroupProposals.Names(f).Contains(b, StringComparer.OrdinalIgnoreCase))).Guild;
        var several = found.Select(f => f.Guild.Id).Distinct().Count() > 1;
        var candidates = found.Where(f => server is null || f.Guild.Id == server.Id).ToList();
        return GamePicks.Complete(text, candidates, f => several ? $"{GameDirectory.Label(f.Game)} · {f.Guild.Name}" : GameDirectory.Label(f.Game), GroupProposals.Names);
    }
}

public sealed class GroupProposalButtons(GroupProposals proposals) : ComponentInteractionModule<ButtonInteractionContext>
{
    // A vote can settle it, which posts in the server and signs people up.
    [ComponentInteraction("groupvote")]
    public async Task VoteAsync(long proposalId, int choice)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        await ShowAsync(await proposals.VoteAsync(proposalId, Context.User, choice));
    }

    [ComponentInteraction("groupsuggest")]
    public InteractionCallbackProperties Suggest(long proposalId)
        => InteractionCallback.Modal(new ModalProperties($"groupsuggestform:{proposalId}", "Suggest a time")
        {
            new LabelProperties("When, in your time", new TextInputProperties("when", TextInputStyle.Short) { MaxLength = 60, Placeholder = "sat 21:00, tomorrow 8pm" }),
        });

    [ComponentInteraction("groupgame")]
    public async Task<InteractionCallbackProperties> PickAsync(long proposalId, long gameId)
        => Answer(await proposals.PickAsync(proposalId, Context.User, gameId));

    // Planning posts in the server and signs people up, which can take longer than an answer may.
    [ComponentInteraction("groupplan")]
    public async Task PlanAsync(long proposalId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        await ShowAsync(await proposals.PlanAsync(proposalId, Context.User));
    }

    private async Task ShowAsync((ProposalView? View, string? Problem) result)
    {
        if (result.View is { } view)
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
        else
            await FollowupAsync(Replies.Ephemeral(result.Problem!));
    }

    [ComponentInteraction("groupsuggestmove")]
    public InteractionCallbackProperties SuggestMove(long proposalId)
        => InteractionCallback.Modal(new ModalProperties($"groupsuggestmoveform:{proposalId}", "Suggest another time")
        {
            new LabelProperties("When, in your time", new TextInputProperties("when", TextInputStyle.Short) { MaxLength = 60, Placeholder = "sat 21:00, tomorrow 8pm" }),
        });

    [ComponentInteraction("groupworks")]
    public async Task WorksAsync(long proposalId, long suggestionId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        await AnswerAsync(await proposals.WorksForAsync(proposalId, Context.User, suggestionId));
    }

    [ComponentInteraction("groupmoveto")]
    public async Task MoveToAsync(long proposalId, long suggestionId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        await AnswerAsync(await proposals.MoveToAsync(proposalId, Context.User, suggestionId));
    }

    private async Task AnswerAsync((ProposalView? View, string? Said) result)
    {
        if (result.View is { } view)
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
        if (result.Said is { } said)
            await FollowupAsync(Replies.Ephemeral(said));
    }

    // Signing up updates the session's card in the server first.
    [ComponentInteraction("groupgo")]
    public async Task GoAsync(long proposalId, string answer)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var (view, said) = await proposals.GoAsync(proposalId, Context.User, answer);
        if (view is not null)
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
        if (said is not null)
            await FollowupAsync(Replies.Ephemeral(said));
    }

    private static InteractionCallbackProperties Answer((ProposalView? View, string? Problem) result)
        => result.View is { } view
            ? InteractionCallback.ModifyMessage(m => (m.Content, m.Components) = (view.Content, view.Rows))
            : InteractionCallback.Message(Replies.Ephemeral(result.Problem!));
}

// A suggested time: added to the proposal, which it can settle.
public sealed class GroupSuggestForm(GroupProposals proposals) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("groupsuggestform")]
    public async Task SuggestAsync(long proposalId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var when = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        var (view, problem) = await proposals.SuggestAsync(proposalId, Context.User, when);
        if (view is null)
            await FollowupAsync(Replies.Ephemeral(problem!));
        else
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
    }
}

// Another time for a planned proposal's session, suggested from the group.
public sealed class GroupSuggestMoveForm(GroupProposals proposals) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("groupsuggestmoveform")]
    public async Task SuggestAsync(long proposalId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var when = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        var (view, said) = await proposals.SuggestMoveAsync(proposalId, Context.User, when);
        if (view is not null)
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
        if (said is not null)
            await FollowupAsync(Replies.Ephemeral(said));
    }
}

// Group proposals whose vote has closed are settled by their rule, a minute at most after.
public sealed class GroupProposalSweep(GroupProposals proposals, TimeProvider time, ILogger<GroupProposalSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await proposals.SweepAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Settling group proposals failed");
            }
        }
    }
}

// What a member answered to group proposals for a server.
public sealed class GroupProposalsMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Group proposals";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Mine(db, guildId, userId).LongCountAsync() + await Picked(db, guildId, userId).LongCountAsync();
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var x in Mine(db, guildId, userId).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("proposal", x.Title);
            if (x.Choice == GroupProposals.CantMake)
                json.WriteString("answer", "can't");
            else
                json.WriteString("canMake", x.Times[x.Choice]);
            json.WriteString("as", x.Name);
            json.WriteEndObject();
        }
        await foreach (var x in Picked(db, guildId, userId).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("proposal", x.Title);
            json.WriteString("wouldPlay", x.Game);
            json.WriteString("as", x.Name);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // Ordered before the row is made: Postgres can't be asked to order by a record's member.
    private static IQueryable<VoteRow> Mine(BotDbContext db, ulong guildId, ulong userId)
        => db.GroupProposalVotes.AsNoTracking().Where(v => v.UserId == userId)
            .Join(db.GroupProposals.AsNoTracking().Where(p => p.GuildId == guildId), v => v.ProposalId, p => p.Id, (v, p) => new { p.Id, p.Title, p.Times, v.Choice, v.Name })
            .OrderBy(x => x.Id).ThenBy(x => x.Choice)
            .Select(x => new VoteRow(x.Id, x.Title, x.Times, x.Choice, x.Name));

    private static IQueryable<PickRow> Picked(BotDbContext db, ulong guildId, ulong userId)
        => db.GroupProposalGameVotes.AsNoTracking().Where(v => v.UserId == userId)
            .Join(db.GroupProposals.AsNoTracking().Where(p => p.GuildId == guildId), v => v.ProposalId, p => p.Id, (v, p) => new { p.Id, p.Title, v.GameId, v.Name })
            .OrderBy(x => x.Id).ThenBy(x => x.GameId)
            .Select(x => new PickRow(x.Id, x.Title, db.Games.Where(g => g.Id == x.GameId).Select(g => g.Name).FirstOrDefault() ?? "a game since removed", x.Name));

    private sealed record VoteRow(long ProposalId, string? Title, DateTimeOffset[] Times, int Choice, string Name);

    private sealed record PickRow(long ProposalId, string? Title, string Game, string Name);
}
