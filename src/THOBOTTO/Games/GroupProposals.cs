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

// Sessions proposed in a group DM by someone who installed the bot to their own account. The group votes there with
// buttons; the bot isn't in the group, so it can't post there or read it, and the message changes only when someone
// presses. The proposer plans the favourite time as a session in the server, which signs up the voters who are members.
public sealed class GroupProposals(
    IDbContextFactory<BotDbContext> dbFactory,
    GatewayClient gateway,
    GameDirectory games,
    GameSessions sessions,
    EventBoard board,
    PanelAccess members,
    ModuleState modules,
    TimeZones zones,
    TimeProvider time)
{
    public const int CantMake = -1;

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

    public async Task<(ProposalView? View, string? Problem)> ProposeAsync(User user, string choice, string when, string? title)
    {
        if (choice.Split(':') is not [var g, var id] || !ulong.TryParse(g, out var guildId) || !long.TryParse(id, out var gameId)
            || await members.InAsync(guildId, user.Id) is not var (guild, member) || await games.FindAsync(guildId, gameId) is not { } game)
            return (null, "Pick one of the listed games.");
        if (game.ChannelId is null)
            return (null, $"{GameDirectory.Label(game)} has no sessions channel in {guild.Name} yet: someone with `games.manage` sets one with `/setup games edit` or on the panel's Games page.");
        if (await sessions.RefusalAsync(guild, member, gameId) is { } refusal)
            return (null, refusal);

        var (zone, own) = await zones.ForAsync(guildId, user.Id);
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
            GuildId = guildId,
            GameId = gameId,
            ProposerId = user.Id,
            ProposerName = NameOf(user),
            Title = string.IsNullOrWhiteSpace(title) ? GameSessions.DefaultTitle(game, null) : title.Trim(),
            Times = [.. times.Distinct().Order()],
            CreatedAt = time.GetUtcNow(),
        };
        db.GroupProposals.Add(proposal);
        await db.SaveChangesAsync();
        return (View(proposal, [], guild.Name, game), null);
    }

    // A time pressed again is taken back; Can't takes back every time, and a time takes back Can't.
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
            }
            foreach (var vote in mine)
                vote.Name = NameOf(user);
            await db.SaveChangesAsync();
        }
        return (await ViewAsync(db, proposal), null);
    }

    // The proposer's: the time most can make (the earliest of a tie) becomes a session in the game's channel.
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
        if (Favourite(proposal.Times, votes, time.GetUtcNow()) is not { } chosen)
            return (null, "All its times have passed: propose new ones.");
        var game = await games.FindAsync(proposal.GuildId, proposal.GameId);
        var (e, problem) = await sessions.CreateAsync(guild, member, proposal.GameId, [proposal.Times[chosen]], proposal.Title, EventVoice.None,
            pingRole: true, poll: false, modeId: null, players: null, channelId: game?.ChannelId ?? 0);
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

    public static int? Favourite(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<GroupProposalVote> votes, DateTimeOffset now)
        => Enumerable.Range(0, times.Count).Where(i => times[i] > now)
            .OrderByDescending(i => votes.Count(v => v.Choice == i)).ThenBy(i => times[i])
            .Select(i => (int?)i).FirstOrDefault();

    private async Task<ProposalView> ViewAsync(BotDbContext db, GroupProposal proposal)
    {
        var votes = await db.GroupProposalVotes.AsNoTracking().Where(v => v.ProposalId == proposal.Id).OrderBy(v => v.Name).ToListAsync();
        var server = gateway.Cache.Guilds.TryGetValue(proposal.GuildId, out var guild) ? guild.Name : "the server";
        var game = await games.FindAsync(proposal.GuildId, proposal.GameId);
        if (proposal is { EventId: { } eventId, Planned: { } planned })
        {
            var e = await board.FindAsync(proposal.GuildId, eventId);
            var at = e?.StartsAt ?? proposal.Times[planned];
            var signed = new List<string>();
            var outside = new List<string>();
            foreach (var vote in votes.Where(v => v.Choice == planned))
                (await members.InAsync(proposal.GuildId, vote.UserId) is null ? outside : signed).Add(vote.Name);
            var link = e is { MessageId: { } message } ? $" · [the session]({Notifier.Link(e.GuildId, e.ChannelId, message)})" : "";
            return new(
                $"✅ **{proposal.Title}** is planned in **{server}**: {When(at)}{link}"
                + (signed.Count > 0 ? $"\nSigned up there: {string.Join(", ", signed)}" : "")
                + (outside.Count > 0 ? $"\nNot in the server: {string.Join(", ", outside)}" : ""),
                []);
        }
        return View(proposal, votes, server, game);
    }

    public static ProposalView View(GroupProposal proposal, IReadOnlyList<GroupProposalVote> votes, string server, Game? game)
    {
        var several = proposal.Times.Length > 1;
        var named = game is null || proposal.Title == GameSessions.DefaultTitle(game, null) ? "" : $" ({GameDirectory.Label(game)})";
        var lines = new List<string> { $"📅 **{proposal.Title}**{named} in **{server}**, proposed by {proposal.ProposerName}" };
        for (var i = 0; i < proposal.Times.Length; i++)
        {
            var can = votes.Where(v => v.Choice == i).Select(v => v.Name).ToList();
            lines.Add($"{(several ? Keycap(i) + " " : "")}{When(proposal.Times[i])}: {(can.Count == 0 ? "nobody yet" : string.Join(", ", can))}");
        }
        if (votes.Where(v => v.Choice == CantMake).Select(v => v.Name).ToList() is { Count: > 0 } cant)
            lines.Add($"Can't: {string.Join(", ", cant)}");
        lines.Add($"-# {(several ? "Press the times you can make" : "Press I'm in if you can make it")} (again to take it back). {proposal.ProposerName} plans it in the server with Plan it.");

        var buttons = several
            ? Enumerable.Range(0, proposal.Times.Length).Select(i => new ButtonProperties($"groupvote:{proposal.Id}:{i}", EmojiProperties.Standard(GameDirectory.ButtonEmoji(Keycap(i))!), ButtonStyle.Secondary)).ToList()
            : [new ButtonProperties($"groupvote:{proposal.Id}:0", "I'm in", ButtonStyle.Success)];
        buttons.Add(new ButtonProperties($"groupvote:{proposal.Id}:{CantMake}", "Can't", ButtonStyle.Secondary));
        buttons.Add(new ButtonProperties($"groupplan:{proposal.Id}", "Plan it", ButtonStyle.Primary));
        return new(string.Join('\n', lines), [.. buttons.Chunk(5).Select(row => new ActionRowProperties(row))]);
    }

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
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null)
    {
        var (view, problem) = await proposals.ProposeAsync(Context.User, game, when, title);
        return InteractionCallback.Message(view is null
            ? Replies.Ephemeral(problem!)
            : new() { Content = view.Content, Components = view.Rows, AllowedMentions = AllowedMentionsProperties.None });
    }
}

// The games of every server the person shares with the bot, named with their server when there are several.
public sealed class GroupGameAutocomplete(GroupProposals proposals) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var found = await proposals.GamesForAsync(context.User.Id);
        var several = found.Select(f => f.Guild.Id).Distinct().Count() > 1;
        return found
            .Where(f => f.Game.Name.Contains(input, StringComparison.OrdinalIgnoreCase) || several && f.Guild.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(f => new ApplicationCommandOptionChoiceProperties(Clip(several ? $"{GameDirectory.Label(f.Game)} · {f.Guild.Name}" : GameDirectory.Label(f.Game)), $"{f.Guild.Id}:{f.Game.Id}"));
    }

    private static string Clip(string text) => text.Length <= 100 ? text : text[..100];
}

public sealed class GroupProposalButtons(GroupProposals proposals) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("groupvote")]
    public async Task<InteractionCallbackProperties> VoteAsync(long proposalId, int choice)
        => Answer(await proposals.VoteAsync(proposalId, Context.User, choice));

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
        return await Mine(db, guildId, userId).LongCountAsync();
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
        json.WriteEndArray();
    }

    private static IQueryable<VoteRow> Mine(BotDbContext db, ulong guildId, ulong userId)
        => db.GroupProposalVotes.AsNoTracking().Where(v => v.UserId == userId)
            .Join(db.GroupProposals.AsNoTracking().Where(p => p.GuildId == guildId), v => v.ProposalId, p => p.Id, (v, p) => new VoteRow(p.Id, p.Title, p.Times, v.Choice, v.Name))
            .OrderBy(x => x.ProposalId).ThenBy(x => x.Choice);

    private sealed record VoteRow(long ProposalId, string Title, DateTimeOffset[] Times, int Choice, string Name);
}
