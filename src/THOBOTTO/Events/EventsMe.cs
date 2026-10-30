using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Events;

namespace THOBOTTO.Commands;

public sealed partial class MeCommands
{
    [SubSlashCommand("calendar", "Your own calendar link: events from your servers, as you choose them")]
    public async Task<InteractionMessageProperties> CalendarAsync(
        [SlashCommandParameter(Name = "from-this-server", Description = "What it shows from this server")] CalendarChoice? choice = null,
        [SlashCommandParameter(Name = "new-link", Description = "Make a new link; the old one stops working")] bool newLink = false)
    {
        var calendars = Get<CalendarLinks>();
        if (choice is { } chosen)
            await calendars.SetChoiceAsync(Context.User.Id, GuildId, chosen switch
            {
                CalendarChoice.Games => CalendarChoices.Games,
                CalendarChoice.Mine => CalendarChoices.Mine,
                CalendarChoice.Nothing => CalendarChoices.Nothing,
                _ => CalendarChoices.Everything,
            });
        var link = await calendars.MemberLinkAsync(Context.User.Id, renew: newLink);
        var shows = CalendarChoices.All.First(c => c.Value == CalendarLinks.ChoiceIn(link, GuildId)).Label;
        return Replies.Ephemeral(await calendars.AddressOfAsync(link) is var (https, webcal)
            ? $"{(newLink ? "New link; the old one stopped working.\n" : "")}Your calendar link (keep it to yourself):\n{https}\nFrom this server: {shows}.\n-# Google Calendar: Other calendars → From URL. Apple: {webcal}"
            : "The panel's address isn't known yet, so there's no link to give.");
    }
}
