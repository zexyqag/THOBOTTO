using NetCord.Rest;

using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class EventOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly EventRules Rules = new();

    private static Event Plan(DateTimeOffset? startsAt, bool choosingGame) => new()
    {
        Id = 9,
        GuildId = 1,
        CreatorId = 3,
        Title = "Game night",
        StartsAt = startsAt,
        PollClosesAt = startsAt is null ? Now.AddHours(5) : null,
        ChoosingGame = choosingGame,
    };

    private static List<(string Id, IReadOnlyList<string> Chosen, string? Typed)> Fields(ModalProperties form)
        => [.. form.OfType<LabelProperties>().Select(l => l.Component switch
        {
            StringMenuProperties menu => (menu.CustomId, (IReadOnlyList<string>)[.. menu.Options.Where(o => o.Default == true).Select(o => o.Value)], (string?)null),
            TextInputProperties text => (text.CustomId, [], text.Value),
            _ => ("?", [], null),
        })];

    [Fact]
    public void Each_stage_asks_what_it_needs_within_five()
    {
        Assert.Equal(["decide", "enough", "closes", "suggestions", "voice"], Fields(EventOptions.Form("x", Plan(null, choosingGame: true), Rules, Now)).Select(f => f.Id));
        Assert.Equal(["decide", "enough", "suggestions", "move", "voice"], Fields(EventOptions.Form("x", Plan(Now.AddDays(1), choosingGame: true), Rules, Now)).Select(f => f.Id));
        Assert.Equal(["suggestions", "move", "voice", "length"], Fields(EventOptions.Form("x", Plan(Now.AddDays(1), choosingGame: false), Rules, Now)).Select(f => f.Id));
    }

    [Fact]
    public void It_opens_with_what_the_plan_has()
    {
        var e = Plan(Now.AddDays(1), choosingGame: true);
        (e.Decide, e.Enough, e.VoiceMode, e.Move) = (Deciding.AsSoonAsEnough, 3, VoiceModes.Locked, Moving.PlannerMoves);
        var fields = Fields(EventOptions.Form("x", e, Rules, Now)).ToDictionary(f => f.Id);

        Assert.Equal([Deciding.AsSoonAsEnough], fields["decide"].Chosen);
        Assert.Equal("3", fields["enough"].Typed);
        Assert.Equal([VoiceModes.Locked], fields["voice"].Chosen);
        Assert.Equal([Moving.PlannerMoves], fields["move"].Chosen);
        Assert.Equal([Suggesting.TimesAndGames], fields["suggestions"].Chosen);
    }

    [Fact]
    public void A_vote_keeps_its_closing_time_unless_another_is_picked()
    {
        var closes = Fields(EventOptions.Form("x", Plan(null, choosingGame: false), Rules, Now)).Single(f => f.Id == "closes");
        Assert.Equal(["keep"], closes.Chosen);
    }

    [Fact]
    public void Only_what_the_form_had_changes()
    {
        var e = Plan(null, choosingGame: true);
        (e.Enough, e.VoiceMode, e.Move) = (3, VoiceModes.Open, Moving.PlannerMoves);
        new PlanOptions(Deciding.PlannerDecides, null, EnoughGiven: true, CloseInHours: 48, Suggestions: Suggesting.Nothing, Move: null, Voice: EventOptions.NoVoice,
            LengthMinutes: null, LengthGiven: false).ApplyTo(e, Now);

        Assert.Equal(Deciding.PlannerDecides, e.Decide);
        Assert.Null(e.Enough);
        Assert.Equal(Now.AddHours(48), e.PollClosesAt);
        Assert.Equal(Suggesting.Nothing, e.Suggestions);
        Assert.Equal(Moving.PlannerMoves, e.Move);
        Assert.Null(e.VoiceMode);
    }

    [Fact]
    public void Stages_follow_the_time_and_the_game()
    {
        Assert.Equal(EventStage.Time, EventStages.Of(Plan(null, choosingGame: true)));
        Assert.Equal(EventStage.Game, EventStages.Of(Plan(Now, choosingGame: true)));
        Assert.Equal(EventStage.Set, EventStages.Of(Plan(Now, choosingGame: false)));
    }
}
