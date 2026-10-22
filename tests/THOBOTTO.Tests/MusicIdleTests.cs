using THOBOTTO.Helpers;
using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class MusicIdleTests
{
    private static readonly MusicRules Default = new();
    private static readonly MusicRules Company = new() { StayWithCompany = true, LonelyMinutes = 0 };

    [Fact]
    public void Nothing_playing_for_the_idle_time_ends_it()
        => Assert.Equal(Moments.Finished, MusicIdle.LeaveReason(Default, playing: false, TimeSpan.FromMinutes(3), listeners: 2, lonelyFor: null));

    [Fact]
    public void Nothing_playing_but_company_keeps_it_when_the_server_wants()
        => Assert.Null(MusicIdle.LeaveReason(Company, playing: false, TimeSpan.FromHours(2), listeners: 1, lonelyFor: null));

    [Fact]
    public void Company_counts_for_nothing_by_default()
        => Assert.Equal(Moments.Finished, MusicIdle.LeaveReason(Default, playing: false, TimeSpan.FromMinutes(3), listeners: 1, lonelyFor: null));

    [Fact]
    public void Nobody_left_for_the_lonely_time_ends_it_even_while_playing()
    {
        Assert.Null(MusicIdle.LeaveReason(Default, playing: true, TimeSpan.Zero, listeners: 0, lonelyFor: TimeSpan.FromMinutes(2)));
        Assert.Equal(Moments.Lonely, MusicIdle.LeaveReason(Default, playing: true, TimeSpan.Zero, listeners: 0, lonelyFor: TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void A_lonely_time_of_zero_goes_at_once()
        => Assert.Equal(Moments.Lonely, MusicIdle.LeaveReason(Company, playing: false, TimeSpan.Zero, listeners: 0, lonelyFor: TimeSpan.Zero));

    [Fact]
    public void Playing_to_people_stays()
        => Assert.Null(MusicIdle.LeaveReason(Default, playing: true, TimeSpan.FromHours(1), listeners: 3, lonelyFor: null));
}
