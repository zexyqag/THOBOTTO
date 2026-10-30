using THOBOTTO.Events;

namespace THOBOTTO.DatabaseTests;

[Collection(DatabaseCollection.Name)]
public class CalendarLinkTests(TestDatabase database)
{
    // The panel's address isn't asked for here.
    private CalendarLinks Links() => new(database, null!, TimeProvider.System);

    [Fact]
    public async Task A_server_has_one_link_until_it_is_renewed()
    {
        var links = Links();
        var first = await links.ServerLinkAsync(907);
        Assert.Equal(first.Token, (await links.ServerLinkAsync(907)).Token);
        var renewed = await links.ServerLinkAsync(907, renew: true);
        Assert.NotEqual(first.Token, renewed.Token);
        Assert.NotEqual(renewed.Token, (await links.ServerLinkAsync(908)).Token);
    }

    [Fact]
    public async Task A_member_chooses_per_server_and_keeps_it_through_a_new_link()
    {
        var links = Links();
        Assert.Equal(CalendarChoices.Everything, CalendarLinks.ChoiceIn(await links.MemberLinkAsync(31), 907));
        await links.SetChoiceAsync(31, 907, CalendarChoices.Mine);
        await links.SetChoiceAsync(31, 908, CalendarChoices.Nothing);
        var renewed = await links.MemberLinkAsync(31, renew: true);
        Assert.Equal(CalendarChoices.Mine, CalendarLinks.ChoiceIn(renewed, 907));
        Assert.Equal(CalendarChoices.Nothing, CalendarLinks.ChoiceIn(renewed, 908));
        Assert.Equal(CalendarChoices.Everything, CalendarLinks.ChoiceIn(renewed, 909));
    }

    [Fact]
    public async Task A_member_reminds_half_an_hour_before_until_they_choose_and_keeps_it_through_a_new_link()
    {
        var links = Links();
        Assert.Equal(30, (await links.MemberLinkAsync(32)).ReminderMinutes);
        await links.SetReminderAsync(32, 1440);
        Assert.Equal(1440, (await links.MemberLinkAsync(32, renew: true)).ReminderMinutes);
        await links.SetReminderAsync(32, null);
        Assert.Null((await links.MemberLinkAsync(32)).ReminderMinutes);
    }
}
