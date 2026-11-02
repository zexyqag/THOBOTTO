using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Events;

// What an Options form says; null for a field it didn't have. Close: hours from now ("keep" leaves it).
public sealed record PlanOptions(string? Decide, int? Enough, bool EnoughGiven, int? CloseInHours, string? Suggestions, string? Move, string? Voice,
    int? LengthMinutes, bool LengthGiven)
{
    public void ApplyTo(Event e, DateTimeOffset now)
    {
        e.Decide = Decide ?? e.Decide;
        if (EnoughGiven)
            e.Enough = Enough;
        if (CloseInHours is { } hours)
            e.PollClosesAt = now + TimeSpan.FromHours(hours);
        e.Suggestions = Suggestions ?? e.Suggestions;
        e.Move = Move ?? e.Move;
        if (Voice is not null)
            e.VoiceMode = Voice == EventOptions.NoVoice ? null : Voice;
    }
}

// A plan's Options, for its planner (or events.manage): the rules and details left out of /event plan, /session plan
// and /propose, as fields its stage needs (a form holds five).
public static class EventOptions
{
    public const string NoVoice = "none";
    private const string Keep = "keep";
    private static readonly int[] ClosingHours = [1, 3, 6, 12, 24, 48, 72, 168];

    public static ModalProperties Form(string customId, Event e, EventRules rules, DateTimeOffset now)
    {
        var stage = EventStages.Of(e);
        var form = new ModalProperties(customId, "Options");
        if (stage != EventStage.Set)
        {
            form.Add(DecideField(e.Decide, game: stage == EventStage.Game));
            form.Add(EnoughField(e.Enough, e.Capacity ?? rules.EnoughPeople));
        }
        if (stage == EventStage.Time)
            form.Add(ClosesField(e.PollClosesAt!.Value - now));
        form.Add(SuggestionsField(e.Suggestions, sessions: e.GameId is not null || e.ChoosingGame));
        if (stage != EventStage.Time)
            form.Add(MoveField(e.Move ?? rules.Move));
        form.Add(new LabelProperties("A voice channel shortly before the start?", Menu("voice", e.VoiceMode ?? NoVoice,
            (NoVoice, "No"), (VoiceModes.Open, "Yes, open to everyone"), (VoiceModes.Locked, "Yes, only for those who are in"))));
        if (stage == EventStage.Set)
            form.Add(new LabelProperties("How long does it last?", new TextInputProperties("length", TextInputStyle.Short)
            {
                Value = e.LengthMinutes is { } minutes ? Durations.Format(TimeSpan.FromMinutes(minutes)) : null,
                Placeholder = $"default: {Durations.Format(TimeSpan.FromMinutes(rules.UsualMinutes))}",
                Required = false,
                MaxLength = 10,
            }));
        return form;
    }

    public static LabelProperties DecideField(string rule, bool game)
        => new("How is it settled?", Menu("decide", rule,
            (Deciding.WhenItCloses, game ? "Most votes when the reminder goes out" : "Most votes when the vote closes"),
            (Deciding.AsSoonAsEnough, game ? "As soon as enough would play one game" : "As soon as enough can make a time"),
            (Deciding.PlannerDecides, "Its planner settles it")));

    public static LabelProperties EnoughField(int? enough, int usual)
        => new("People needed, for \"as soon as enough\"", new TextInputProperties("enough", TextInputStyle.Short)
        {
            Value = enough?.ToString(),
            Placeholder = $"default: {usual}",
            Required = false,
            MaxLength = 3,
        });

    public static LabelProperties ClosesField(TimeSpan left)
        => new("When does the vote close?", Menu("closes", Keep,
            [(Keep, $"As it is: in about {(left.TotalHours >= 1 ? $"{Math.Round(left.TotalHours)} h" : $"{Math.Max(0, Math.Round(left.TotalMinutes))} min")}"),
             .. ClosingHours.Select(h => (h.ToString(), h switch { 1 => "In an hour", 24 => "In a day", 168 => "In a week", < 24 => $"In {h} hours", _ => $"In {h / 24} days" }))]));

    public static LabelProperties SuggestionsField(string suggestions, bool sessions)
        => new("What can others suggest?", sessions
            ? Menu("suggestions", suggestions, (Suggesting.TimesAndGames, "Times and games"), (Suggesting.Times, "Times only"), (Suggesting.Nothing, "Nothing"))
            : Menu("suggestions", suggestions == Suggesting.Nothing ? Suggesting.Nothing : Suggesting.TimesAndGames, (Suggesting.TimesAndGames, "Other times"), (Suggesting.Nothing, "Nothing")));

    public static LabelProperties MoveField(string move)
        => new("When does a suggested change go through?", Menu("move", move,
            (Moving.Majority, "When most of those who are in say it works"), (Moving.PlannerMoves, "Only when its planner makes it")));

    private static StringMenuProperties Menu(string id, string current, params (string Value, string Label)[] choices)
        => new(id, choices.Select(c => new StringMenuSelectOptionProperties(c.Label, c.Value) { Default = c.Value == current })) { MinValues = 1, MaxValues = 1 };

    // What the form says, or why it can't be saved.
    public static (PlanOptions? Options, string? Problem) Read(IReadOnlyList<IComponent> components)
    {
        var fields = components.OfType<Label>().Select(l => l.Component).ToList();
        string? Picked(string id) => fields.OfType<StringMenu>().FirstOrDefault(m => m.CustomId == id)?.SelectedValues is { } values ? values.FirstOrDefault() : null;
        string? Typed(string id) => fields.OfType<TextInput>().FirstOrDefault(t => t.CustomId == id)?.Value.Trim();

        var enoughText = Typed("enough");
        int? enough = null;
        if (enoughText is { Length: > 0 })
        {
            if (!int.TryParse(enoughText, out var people) || people is < 1 or > 500)
                return (null, "People needed is a number from 1 to 500 (or empty for the default).");
            enough = people;
        }
        var lengthText = Typed("length");
        var (minutes, lengthProblem) = EventRules.ReadLength(lengthText is { Length: > 0 } ? lengthText : null);
        if (lengthProblem is not null)
            return (null, lengthProblem);
        var closes = Picked("closes") is { } close && close != Keep && int.TryParse(close, out var hours) ? hours : (int?)null;
        return (new(Picked("decide"), enough, enoughText is not null, closes, Picked("suggestions"), Picked("move"), Picked("voice"), minutes, lengthText is not null), null);
    }

    // Saved to the event: what it says (and what settling now said, when the change made it due).
    public static async Task<string> SaveAsync(EventBoard board, Event e, PlanOptions options, DateTimeOffset now)
    {
        if (options.LengthGiven && options.LengthMinutes != e.LengthMinutes)
            await board.ChangeLengthAsync(e.Id, options.LengthMinutes);
        return await board.ChangeRulesAsync(e.Id, x => options.ApplyTo(x, now));
    }

    public static async Task<string?> NotTheirsAsync(Event e, Guild guild, GuildUser user, AccessControl access)
        => e.CreatorId == user.Id || await access.CanAsync(guild, user, BotPermissions.ManageEvents)
            ? null
            : $"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can change its options.";
}

public sealed class EventOptionButtons(EventBoard board, SettingsStore settings, AccessControl access, TimeProvider time) : GuildButtonModule
{
    [ComponentInteraction("eventoptions")]
    public async Task<InteractionCallbackProperties> OptionsAsync(long eventId)
    {
        if (await board.FindAsync(GuildId, eventId) is not { State: EventStates.Scheduled } e)
            return InteractionCallback.Message(Replies.Ephemeral("It's over or cancelled."));
        if (await EventOptions.NotTheirsAsync(e, Guild, (GuildUser)Context.User, access) is { } refusal)
            return InteractionCallback.Message(Replies.Ephemeral(refusal));
        return InteractionCallback.Modal(EventOptions.Form($"eventoptionsform:{eventId}:{(int)EventStages.Of(e)}", e,
            await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId), time.GetUtcNow()));
    }
}

public sealed class EventOptionsModal(EventBoard board, AccessControl access, TimeProvider time) : GuildModalModule
{
    // The form was for the stage it was at; one that has moved on since asks for a fresh one.
    [ComponentInteraction("eventoptionsform")]
    public async Task SaveAsync(long eventId, int stage)
    {
        var e = await board.FindAsync(GuildId, eventId);
        var (options, problem) = EventOptions.Read(Context.Components);
        var refusal = e is not { State: EventStates.Scheduled } ? "It's over or cancelled."
            : await EventOptions.NotTheirsAsync(e, Guild, (GuildUser)Context.User, access)
            ?? ((int)EventStages.Of(e) != stage ? "It has moved on to the next step meanwhile: open Options again." : problem);
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var said = await EventOptions.SaveAsync(board, e!, options!, time.GetUtcNow());
        await ModifyResponseAsync(m => m.Content = said);
    }
}
