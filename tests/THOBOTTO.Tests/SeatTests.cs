using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class SeatTests
{
    private static EventRsvp Rsvp(ulong user, string status) => new() { UserId = user, Status = status };

    [Theory]
    [InlineData(RsvpStatuses.In, null, 1, 2, RsvpStatuses.In)]
    [InlineData(RsvpStatuses.In, null, 2, 2, RsvpStatuses.Waiting)]
    [InlineData(RsvpStatuses.In, RsvpStatuses.In, 2, 2, RsvpStatuses.In)]
    [InlineData(RsvpStatuses.In, RsvpStatuses.Waiting, 1, 2, RsvpStatuses.Waiting)]
    [InlineData(RsvpStatuses.In, RsvpStatuses.Out, 50, null, RsvpStatuses.In)]
    [InlineData(RsvpStatuses.Maybe, null, 2, 2, RsvpStatuses.Maybe)]
    public void Status_follows_the_room_left(string requested, string? was, int inCount, int? capacity, string expected)
        => Assert.Equal(expected, Seats.Status(requested, was, inCount, capacity));

    [Fact]
    public void Poll_voters_past_the_limit_wait()
    {
        var (going, waiting) = Seats.Split([1, 2, 3], 2);
        Assert.Equal([1UL, 2UL], going);
        Assert.Equal([3UL], waiting);
        Assert.Empty(Seats.Split([1, 2, 3], null).Waiting);
    }

    [Fact]
    public void Another_session_takes_the_opener_then_the_waiting_list()
    {
        var rsvps = new[] { Rsvp(1, RsvpStatuses.In), Rsvp(2, RsvpStatuses.Waiting), Rsvp(3, RsvpStatuses.Maybe), Rsvp(4, RsvpStatuses.Waiting) };
        Assert.Equal([3UL, 2UL], Seats.MovingToAnother(rsvps, 3, 2));
        Assert.Equal([2UL, 4UL], Seats.MovingToAnother(rsvps, 1, 5));
        Assert.Equal([2UL, 4UL], Seats.MovingToAnother(rsvps, 4, 5));
    }
}
