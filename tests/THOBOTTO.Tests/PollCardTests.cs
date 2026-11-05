using NetCord.Rest;

using THOBOTTO.Polls;

namespace THOBOTTO.Tests;

public class PollCardTests
{
    private static LivePoll Live(bool closed, params (ulong User, int Option)[] votes)
        => new(new Poll
        {
            Id = 4,
            GuildId = 1,
            ChannelId = 2,
            AskerId = 3,
            Question = "Pizza?",
            Options = ["Yes", "No", "Maybe", "Later", "Never", "Soon"],
            ClosesAt = DateTimeOffset.FromUnixTimeSeconds(1000),
            Closed = closed,
        }, votes.Select(v => new PollVote { PollId = 4, UserId = v.User, Option = v.Option }));

    [Fact]
    public void An_open_poll_shows_bars_and_a_button_per_answer_five_a_row_then_close()
    {
        var (embed, components) = PollBook.Card(Live(false, (7, 0), (8, 0), (9, 1)));

        Assert.StartsWith("▰▰▰▰▰▰▰▱▱▱ **Yes** · 2 (66%)\n▰▰▰▱▱▱▱▱▱▱ **No** · 1 (33%)", embed.Description);
        Assert.EndsWith("Asked by <@3> · 3 votes · closes <t:1000:R>", embed.Description);
        var rows = components.Cast<ActionRowProperties>().Select(r => r.Select(b => ((ButtonProperties)b).CustomId).ToList()).ToList();
        Assert.Equal([["pollvote:4:0", "pollvote:4:1", "pollvote:4:2", "pollvote:4:3", "pollvote:4:4"], ["pollvote:4:5"], ["pollclose:4"]], rows);
    }

    [Fact]
    public void A_closed_poll_is_its_result_without_buttons()
    {
        var (embed, components) = PollBook.Card(Live(true, (7, 1)));

        Assert.StartsWith("▫️ **Yes** · 0 (0%)\n🏆 **No** · 1 (100%)", embed.Description);
        Assert.Contains("1 vote · closed <t:1000:R>", embed.Description);
        Assert.Empty(components);
    }
}
