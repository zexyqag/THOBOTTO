using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Discord;
using THOBOTTO.Events;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Steam;

namespace THOBOTTO.Games;

// Planning from the context menus: a message ("valheim sat 20:00?") or a member (what you can both play), opening the
// plan form with what they say filled in. In a server the bot is in it plans a session; elsewhere (DMs, group DMs, other
// servers, for those who added the bot) it proposes one, as /propose does.
public static partial class MessagePlans
{
    public const string ServerForm = "planfrommessage";
    public const string GroupForm = "proposefrommessage";

    // The games the text names (whole words, any case) and the first time in it, as typed.
    public static (IReadOnlyList<string> Games, string When) Guess(string text, IReadOnlyList<string> names, DateTimeZone zone, Instant now)
    {
        var games = names.Where(n => Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(n)}(?!\w)", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(EventBoard.MaxGames).ToList();
        var words = Word().Matches(text).Select(m => m.Value.TrimEnd('.', ':')).Where(w => w.Length > 0).Take(60).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            for (var length = Math.Min(4, words.Count - i); length > 0; length--)
            {
                var candidate = string.Join(' ', words.Skip(i).Take(length));
                if (WhenParser.Parse(candidate, zone, now).At is not null)
                    return (games, candidate);
            }
        }
        return (games, "");
    }

    public static ModalProperties Form(string id, IReadOnlyList<string> games, string when, string title) => new(id, title)
    {
        new LabelProperties("Games (several separated by commas: a vote)", new TextInputProperties("games", TextInputStyle.Short)
        {
            Value = games.Count == 0 ? null : string.Join(", ", games),
            Placeholder = "Valheim, Lethal Company",
            MaxLength = 300,
        }),
        new LabelProperties("When, in your time (several: a vote)", new TextInputProperties("when", TextInputStyle.Short)
        {
            Value = when.Length == 0 ? null : when,
            Placeholder = "now, fri 20:00, tomorrow 19:30",
            MaxLength = 200,
        }),
        new LabelProperties("Title (optional)", new TextInputProperties("title", TextInputStyle.Short) { Required = false, MaxLength = 100 }),
    };

    public static Dictionary<string, string> Inputs(IReadOnlyList<IComponent> components)
        => components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());

    [GeneratedRegex(@"[^\s,!?;()]+")]
    private static partial Regex Word();
}

public sealed class PlanFromMessageCommand(GameDirectory games, GroupProposals proposals, TimeZones zones, TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    [MessageCommand("Plan a session from this", IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
        Contexts = [InteractionContextType.Guild, InteractionContextType.DMChannel])]
    public async Task<InteractionCallbackProperties> PlanAsync(RestMessage message)
    {
        var server = Context.Guild;
        IReadOnlyList<string> names = server is not null
            ? [.. (await games.ListAsync(server.Id)).Select(g => g.Name)]
            : [.. (await proposals.GamesForAsync(Context.User.Id)).Select(f => f.Game.Name).Distinct()];
        if (names.Count == 0)
            return InteractionCallback.Message(Replies.Ephemeral(server is not null ? "This server has no games set up." : GroupProposals.NoGames));
        var (zone, _) = await zones.ForAsync(Context.Interaction.GuildId ?? 0, Context.User.Id);
        var (named, when) = MessagePlans.Guess(message.Content, names, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        return InteractionCallback.Modal(MessagePlans.Form(server is not null ? MessagePlans.ServerForm : MessagePlans.GroupForm, named, when, server is not null ? "Plan a session" : "Propose a session"));
    }
}

// What you and a member both own on Steam, privately, with a way to plan a session in it.
public sealed class PlayTogetherCommand(SteamLibrary library, SteamClient client, ModuleState modules, GameDirectory games) : GuildCommandModule
{
    [UserCommand("Play together", Contexts = [InteractionContextType.Guild])]
    public async Task PlayAsync(User user)
    {
        var refusal = !await modules.IsEnabledAsync(GuildId, SteamLibrary.ModuleId) ? ModuleRegistry.Off(SteamLibrary.ModuleId)
            : !client.Configured ? SteamCommands.NotSetUp
            : user.Id == Context.User.Id ? "That's you: right-click someone else."
            : user.IsBot ? "Bots don't play."
            : null;
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        // Asking the store about the games can take longer than Discord waits.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var common = await library.CommonAsync([Context.User.Id, user.Id], 10);
        var text = SteamCommands.CommonText(common, ids => string.Join(", ", ids.Select(id => $"<@{id}>")), "`/steam link`");
        var plannable = PlayTogether.Plannable(common, await games.ListAsync(GuildId));
        await ModifyResponseAsync(m =>
        {
            (m.Content, m.AllowedMentions) = (text, AllowedMentionsProperties.None);
            m.Components = plannable.Count == 0 ? [] : [new ActionRowProperties { new ButtonProperties($"playtogetherplan:{user.Id}", "Plan a session", ButtonStyle.Primary) }];
        });
    }
}

public static class PlayTogether
{
    // Of the games you can play together, those that are games of the server, by its names.
    public static IReadOnlyList<string> Plannable(CommonGames common, IReadOnlyList<Game> games)
        => [.. games.Where(g => common.Together.Any(t => SteamNames.Same(t.Name, g.Name))).Select(g => g.Name).Take(EventBoard.MaxGames)];
}

public sealed class PlayTogetherButtons(SteamLibrary library, GameDirectory games) : GuildButtonModule
{
    [ComponentInteraction("playtogetherplan")]
    public async Task<InteractionCallbackProperties> PlanAsync(ulong userId)
    {
        var plannable = PlayTogether.Plannable(await library.CommonAsync([Context.User.Id, userId], 10), await games.ListAsync(GuildId));
        return InteractionCallback.Modal(MessagePlans.Form(MessagePlans.ServerForm, plannable, "", "Plan a session"));
    }
}

// The plan form, in a server: a session as /session plan makes it (pinging its game's role).
public sealed class PlanFromMessageModal(GameSessions sessions) : GuildModalModule, IInteractionModule
{
    Guild IInteractionModule.Guild => Guild;
    User IInteractionModule.User => Context.User;
    ulong IInteractionModule.ChannelId => Context.Channel.Id;
    Task IInteractionModule.RespondAsync(InteractionCallbackProperties callback) => RespondAsync(callback);
    Task IInteractionModule.ModifyResponseAsync(Action<MessageOptions> modify) => ModifyResponseAsync(modify);

    [ComponentInteraction(MessagePlans.ServerForm)]
    public async Task PlanAsync()
    {
        var inputs = MessagePlans.Inputs(Context.Components);
        var (picked, problem) = await sessions.PickAsync(GuildId, inputs.GetValueOrDefault("games") ?? "");
        if (problem is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(problem)));
            return;
        }
        await sessions.StartAsync(this, picked, (inputs.GetValueOrDefault("when") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            inputs.GetValueOrDefault("title"), pingRole: true);
    }
}

// The plan form anywhere else: a proposal posted where the message is.
public sealed class ProposeFromMessageModal(GroupProposals proposals) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction(MessagePlans.GroupForm)]
    public async Task<InteractionCallbackProperties> ProposeAsync()
    {
        var inputs = MessagePlans.Inputs(Context.Components);
        return GroupProposals.Posted(await proposals.ProposeAsync(Context.User, inputs.GetValueOrDefault("games") ?? "", inputs.GetValueOrDefault("when") ?? "", inputs.GetValueOrDefault("title")));
    }
}
