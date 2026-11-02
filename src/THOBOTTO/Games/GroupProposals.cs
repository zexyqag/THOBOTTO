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

// A planned proposal's session as the server has it now; State null when it's gone.
public sealed record PlannedSession(string Title, string Server, string? State, DateTimeOffset At, string? Link,
    IReadOnlyList<string> In, IReadOnlyList<string> Waiting, IReadOnlyList<string> Outside);

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
    TimeProvider time)
{
    public const int CantMake = -1;
    public const int MaxGames = 5;

    // How long after its last time a proposal is kept, to answer a late press.
    private static readonly TimeSpan Kept = TimeSpan.FromDays(7);

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
    public async Task<(ProposalView? View, string? Problem)> ProposeAsync(User user, IReadOnlyList<string> choices, string when, string? title)
    {
        var picked = choices.Select(c => c.Split(':') is [var g, var id] && ulong.TryParse(g, out var guildId) && long.TryParse(id, out var gameId) ? (guildId, gameId) : default).ToList();
        if (picked.Count == 0 || picked.Contains(default))
            return (null, "Pick one of the listed games.");
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
            if (game.ChannelId is null && rules.SessionsChannelId is null)
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
            CreatedAt = time.GetUtcNow(),
        };
        db.GroupProposals.Add(proposal);
        await db.SaveChangesAsync();
        return (View(proposal, [], [], guild.Name, found), null);
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
        }
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

    // The proposer's: the time most can make (the earliest of a tie) in the game most would play (the first of a tie)
    // becomes a session in the game's channel.
    public async Task<(ProposalView? View, string? Problem)> PlanAsync(long proposalId, User user)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupProposals.FindAsync(proposalId) is not { } proposal)
            return (null, "This proposal is gone.");
        if (proposal.EventId is not null)
            return (await ViewAsync(db, proposal), null);
        if (proposal.ProposerId != user.Id)
            return (null, $"Only {proposal.ProposerName} can plan it, having proposed it.");
        if (await members.InAsync(proposal.GuildId, user.Id) is not var (guild, member))
            return (null, "You're no longer in that server.");

        var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposalId).ToListAsync();
        var picks = await db.GroupProposalGameVotes.AsNoTracking().Where(v => v.ProposalId == proposalId).ToListAsync();
        if (Favourite(proposal.Times, votes, time.GetUtcNow()) is not { } chosen)
            return (null, "All its times have passed: propose new ones.");
        if (FavouriteGame([.. (await GamesOfAsync(proposal)).Select(g => g.Id)], picks) is not { } gameId)
            return (null, "Its games are gone from the server: propose others.");
        var (e, problem) = await sessions.CreateAsync(guild, member, gameId, [proposal.Times[chosen]], proposal.Title, voice: null,
            pingRole: true, poll: false, modeId: null, players: null, channelId: 0);
        if (e is null)
            return (null, problem);

        (proposal.EventId, proposal.Planned) = (e.Id, chosen);
        await db.SaveChangesAsync();
        foreach (var vote in votes.Where(v => v.Choice == chosen))
        {
            if (await members.InAsync(proposal.GuildId, vote.UserId) is not null)
                await board.RsvpAsync(e.Id, vote.UserId, RsvpStatuses.In);
        }
        return (await ViewAsync(db, proposal), null);
    }

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

    public static int? Favourite(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<GroupProposalVote> votes, DateTimeOffset now)
        => Enumerable.Range(0, times.Count).Where(i => times[i] > now)
            .OrderByDescending(i => votes.Count(v => v.Choice == i)).ThenBy(i => times[i])
            .Select(i => (int?)i).FirstOrDefault();

    public static long? FavouriteGame(IReadOnlyList<long> gameIds, IReadOnlyList<GroupProposalGameVote> picks)
        => gameIds.OrderByDescending(id => picks.Count(p => p.GameId == id)).Select(id => (long?)id).FirstOrDefault();

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
        if (proposal is not { EventId: { } eventId, Planned: { } planned })
        {
            var picks = await db.GroupProposalGameVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).OrderBy(v => v.Name).ToListAsync();
            return View(proposal, votes, picks, server, found);
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
        return Planned(proposal.Id, new(
            e?.Title ?? proposal.Title ?? (found is [var game, ..] ? GameSessions.DefaultTitle(game, null) : "The session"),
            server,
            e?.State,
            e?.StartsAt ?? proposal.Times[planned],
            e is { MessageId: { } message } ? Notifier.Link(e.GuildId, e.ChannelId, message) : null,
            [.. going.Where(r => r.Status == RsvpStatuses.In).Select(r => Name(r.UserId))],
            [.. going.Where(r => r.Status == RsvpStatuses.Waiting).Select(r => Name(r.UserId))],
            outside));
    }

    public static ProposalView View(GroupProposal proposal, IReadOnlyList<GroupProposalVote> votes, IReadOnlyList<GroupProposalGameVote> picks, string server, IReadOnlyList<Game> found)
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
        lines.Add($"-# {(several ? "Press the times you can make" : "Press I'm in if you can make it")}{(choosing ? " and the games you'd play" : "")} (again to take it back). "
            + $"{proposal.ProposerName} plans it in the server with Plan it.");

        var times = several
            ? Enumerable.Range(0, proposal.Times.Length).Select(i => new ButtonProperties($"groupvote:{proposal.Id}:{i}", EmojiProperties.Standard(GameDirectory.ButtonEmoji(Keycap(i))!), ButtonStyle.Secondary)).ToList()
            : [new ButtonProperties($"groupvote:{proposal.Id}:0", "I'm in", ButtonStyle.Success)];
        ButtonProperties[] end =
        [
            new($"groupvote:{proposal.Id}:{CantMake}", "Can't", ButtonStyle.Secondary),
            new($"groupplan:{proposal.Id}", "Plan it", ButtonStyle.Primary),
        ];
        var rows = choosing
            ? times.Chunk(5).Concat(found.Select(GameButton).Chunk(5)).Append(end)
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
        lines.Add($"-# I'm in signs you up there (if you're in {session.Server}), Can't takes it back. Refresh shows what changed there.");
        return new(string.Join('\n', lines),
        [
            new ActionRowProperties(
            [
                new ButtonProperties($"groupgo:{proposalId}:in", "I'm in", ButtonStyle.Success),
                new ButtonProperties($"groupgo:{proposalId}:out", "Can't", ButtonStyle.Secondary),
                new ButtonProperties($"groupgo:{proposalId}:refresh", "Refresh", ButtonStyle.Secondary),
            ]),
        ]);
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
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GroupGameAutocomplete))] string game,
        [SlashCommandParameter(Description = "When, in your time: fri 8pm, tomorrow 19:30; several with commas to vote on", MaxLength = 400)] string when,
        [SlashCommandParameter(Name = "game2", Description = "Another game of the server to vote on", AutocompleteProviderType = typeof(GroupGameAutocomplete))] string? second = null,
        [SlashCommandParameter(Name = "game3", Description = "Another game of the server to vote on", AutocompleteProviderType = typeof(GroupGameAutocomplete))] string? third = null,
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null)
        => GroupProposals.Posted(await proposals.ProposeAsync(Context.User, [game, .. new[] { second, third }.OfType<string>()], when, title));
}

// The games of every server the person shares with the bot, named with their server when there are several; once a
// game is picked, the others of its server.
public sealed class GroupGameAutocomplete(GroupProposals proposals) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var first = option.Name == "game" ? null : context.Interaction.Data.Options.FirstOrDefault(o => o.Name == "game")?.Value?.Split(':')[0];
        var found = (await proposals.GamesForAsync(context.User.Id)).Where(f => first is null || f.Guild.Id.ToString() == first).ToList();
        var several = found.Select(f => f.Guild.Id).Distinct().Count() > 1;
        return found
            .Where(f => f.Game.Name.Contains(input, StringComparison.OrdinalIgnoreCase) || several && f.Guild.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(f => new ApplicationCommandOptionChoiceProperties(Clip(several ? $"{GameDirectory.Label(f.Game)} · {f.Guild.Name}" : GameDirectory.Label(f.Game)), GroupProposals.Choice(f.Guild, f.Game)));
    }

    private static string Clip(string text) => text.Length <= 100 ? text : text[..100];
}

public sealed class GroupProposalButtons(GroupProposals proposals) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("groupvote")]
    public async Task<InteractionCallbackProperties> VoteAsync(long proposalId, int choice)
        => Answer(await proposals.VoteAsync(proposalId, Context.User, choice));

    [ComponentInteraction("groupgame")]
    public async Task<InteractionCallbackProperties> PickAsync(long proposalId, long gameId)
        => Answer(await proposals.PickAsync(proposalId, Context.User, gameId));

    // Planning posts in the server and signs people up, which can take longer than an answer may.
    [ComponentInteraction("groupplan")]
    public async Task PlanAsync(long proposalId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var (view, problem) = await proposals.PlanAsync(proposalId, Context.User);
        if (view is null)
            await FollowupAsync(Replies.Ephemeral(problem!));
        else
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
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
