using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Panel;
using THOBOTTO.Steam;

namespace THOBOTTO.Games;

public sealed record LineupView(string Content, IReadOnlyList<ActionRowProperties> Rows);

// What a group DM can all play on Steam. The bot isn't told who's in a group, so they're counted as they press Add me
// (or are named by whoever asked, among those sharing a server with Steam on with them). From the games shown, anyone
// can propose a session in those that are games of a server of theirs.
public sealed class GroupLineups(
    IDbContextFactory<BotDbContext> dbFactory,
    SteamLibrary library,
    SteamClient client,
    PanelAccess members,
    ModuleState modules,
    GroupProposals proposals,
    TimeProvider time)
{
    private const int Shown = 10;
    private static readonly TimeSpan Kept = TimeSpan.FromDays(7);

    public async Task<string?> RefusalAsync(ulong userId)
        => !client.Configured ? SteamCommands.NotSetUp
            : (await SteamGuildsAsync(userId)).Count == 0 ? "None of the servers you share with the bot has Steam on."
            : null;

    // A new lineup of the person and those named; and, when some named couldn't be counted, why.
    public async Task<(long Id, string? Note)> StartAsync(User user, IReadOnlyCollection<ulong> named)
    {
        var guilds = await SteamGuildsAsync(user.Id);
        var people = new Dictionary<ulong, string> { [user.Id] = NameOf(user) };
        var unknown = 0;
        foreach (var id in named.Where(id => id != user.Id).Distinct())
        {
            GuildUser? found = null;
            foreach (var guild in guilds)
            {
                if (await members.InAsync(guild.Id, id) is var (_, member))
                {
                    found = member;
                    break;
                }
            }
            if (found is null)
                unknown++;
            else
                people[id] = NameOf(found);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var cutoff = time.GetUtcNow() - Kept;
        await db.GroupLineups.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync();
        var lineup = new GroupLineup { StarterId = user.Id, CreatedAt = time.GetUtcNow() };
        db.GroupLineups.Add(lineup);
        await db.SaveChangesAsync();
        db.GroupLineupMembers.AddRange(people.Select(p => new GroupLineupMember { LineupId = lineup.Id, UserId = p.Key, Name = p.Value }));
        await db.SaveChangesAsync();
        return (lineup.Id, unknown == 0 ? null
            : $"{(unknown == 1 ? "One you named isn't" : $"{unknown} you named aren't")} in a server of yours with Steam on, so only they can add themselves, with Add me.");
    }

    // Pressed again, they're taken off.
    public async Task<(LineupView? View, string? Problem)> ToggleAsync(long lineupId, User user)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.GroupLineups.AnyAsync(l => l.Id == lineupId))
            return (null, Gone);
        if (await db.GroupLineupMembers.FindAsync(lineupId, user.Id) is { } was)
            db.GroupLineupMembers.Remove(was);
        else
            db.GroupLineupMembers.Add(new() { LineupId = lineupId, UserId = user.Id, Name = NameOf(user) });
        await db.SaveChangesAsync();
        return (await ViewAsync(lineupId), null);
    }

    public async Task<LineupView> ViewAsync(long lineupId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var lineup = await db.GroupLineups.FirstAsync(l => l.Id == lineupId);
        var names = await db.GroupLineupMembers.AsNoTracking().Where(m => m.LineupId == lineupId).OrderBy(m => m.Name).ToDictionaryAsync(m => m.UserId, m => m.Name);
        var common = await library.CommonAsync(names.Keys, Shown);
        lineup.Shown = [.. common.Together.Select(g => g.Name)];
        await db.SaveChangesAsync();

        var text = SteamCommands.CommonText(common, ids => string.Join(", ", ids.Select(id => names[id])), "`/steam link` in a server with Steam on")
            + $"\n-# Press Add me to be counted (again to leave){(common.Together.Count > 0 ? "; Propose a session to vote on some of these in a server" : "")}.";
        var buttons = new List<ButtonProperties> { new($"grouplineup:{lineupId}", "Add me", ButtonStyle.Success) };
        if (common.Together.Count > 0)
            buttons.Add(new($"grouplineuppropose:{lineupId}", "Propose a session", ButtonStyle.Primary));
        return new(text, [new ActionRowProperties(buttons)]);
    }

    // The form for proposing a session in some of the games shown: those that are games of the person's servers.
    public async Task<(ModalProperties? Form, string? Problem)> ProposalFormAsync(long lineupId, User user)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GroupLineups.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineupId) is not { } lineup)
            return (null, Gone);
        var games = (await proposals.GamesForAsync(user.Id)).Where(f => lineup.Shown.Any(name => SteamNames.Same(name, f.Game.Name))).Take(25).ToList();
        if (games.Count == 0)
            return (null, "None of these is a game of a server of yours: it's added on the panel's Games page. `/propose` offers the games there are.");
        var several = games.Select(f => f.Guild.Id).Distinct().Count() > 1;
        var options = games.Select(f => new StringMenuSelectOptionProperties(Clip(GameDirectory.Label(f.Game), 100), GroupProposals.Choice(f.Guild, f.Game))
        {
            Description = several ? Clip(f.Guild.Name, 100) : null,
        });
        return (new ModalProperties("grouplineupform", "Propose a session")
        {
            new LabelProperties("Games (with several, the group votes)", new StringMenuProperties("games", options)
            {
                MinValues = 1,
                MaxValues = Math.Min(GroupProposals.MaxGames, games.Count),
            }),
            new LabelProperties("When, in your time", new TextInputProperties("when", TextInputStyle.Short)
            {
                MaxLength = 400,
                Placeholder = "fri 8pm; several with commas to vote on",
            }),
            new LabelProperties("Title (optional)", new TextInputProperties("title", TextInputStyle.Short) { Required = false, MaxLength = 100 }),
        }, null);
    }

    private const string Gone = "This list is gone: `/what-can-we-play` starts a new one.";

    private async Task<IReadOnlyList<Guild>> SteamGuildsAsync(ulong userId)
    {
        var found = new List<Guild>();
        foreach (var guild in await members.GuildsAsync(userId))
        {
            if (await modules.IsEnabledAsync(guild.Id, SteamLibrary.ModuleId))
                found.Add(guild);
        }
        return found;
    }

    private static string NameOf(User user) => user.GlobalName ?? user.Username;

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}

public sealed class GroupLineupCommand(GroupLineups lineups) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("what-can-we-play", "The Steam games you all own and can play together: each of you presses Add me",
        IntegrationTypes = [ApplicationIntegrationType.UserInstall], Contexts = [InteractionContextType.DMChannel])]
    public async Task WhatCanWePlayAsync(
        [SlashCommandParameter(Description = "Who to count besides you, by mentioning them (the others press Add me)")] string? with = null)
    {
        if (await lineups.RefusalAsync(Context.User.Id) is { } refused)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refused)));
            return;
        }
        // Asking the store about the games can take longer than Discord waits.
        await RespondAsync(InteractionCallback.DeferredMessage());
        var named = with is null ? [] : SteamCommands.Mention().Matches(with).Select(m => ulong.Parse(m.Groups[1].Value)).ToList();
        var (id, note) = await lineups.StartAsync(Context.User, named);
        var view = await lineups.ViewAsync(id);
        await ModifyResponseAsync(m => (m.Content, m.Components, m.AllowedMentions) = (view.Content, view.Rows, AllowedMentionsProperties.None));
        if (note is not null)
            await FollowupAsync(Replies.Ephemeral(note));
    }
}

public sealed class GroupLineupButtons(GroupLineups lineups) : ComponentInteractionModule<ButtonInteractionContext>
{
    // Counting someone in can ask the store about more games.
    [ComponentInteraction("grouplineup")]
    public async Task ToggleAsync(long lineupId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var (view, problem) = await lineups.ToggleAsync(lineupId, Context.User);
        if (view is null)
            await FollowupAsync(Replies.Ephemeral(problem!));
        else
            await ModifyResponseAsync(m => (m.Content, m.Components) = (view.Content, view.Rows));
    }

    [ComponentInteraction("grouplineuppropose")]
    public async Task<InteractionCallbackProperties> ProposeAsync(long lineupId)
    {
        var (form, problem) = await lineups.ProposalFormAsync(lineupId, Context.User);
        return form is null ? InteractionCallback.Message(Replies.Ephemeral(problem!)) : InteractionCallback.Modal(form);
    }
}

// The proposal goes to the group as a message of its own.
public sealed class GroupLineupForm(GroupProposals proposals) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("grouplineupform")]
    public async Task<InteractionCallbackProperties> ProposeAsync()
    {
        var parts = Context.Components.OfType<Label>().Select(l => l.Component).ToList();
        var games = parts.OfType<StringMenu>().FirstOrDefault()?.SelectedValues ?? [];
        var text = parts.OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value);
        return GroupProposals.Posted(await proposals.ProposeAsync(Context.User, string.Join(", ", games), text.GetValueOrDefault("when") ?? "", text.GetValueOrDefault("title")));
    }
}
