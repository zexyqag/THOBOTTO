using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using NodaTime;

using THOBOTTO.Events;

namespace THOBOTTO;

public sealed partial class MeCommands
{
    [SubSlashCommand("timezone", "Your time zone, for reading the times you type (leave out to see it)")]
    public async Task<InteractionMessageProperties> TimeZoneAsync(
        [SlashCommandParameter(Description = "Start typing a city, e.g. Copenhagen", AutocompleteProviderType = typeof(TimeZoneAutocomplete))] string? zone = null)
    {
        var zones = Get<TimeZones>();
        if (zone is null)
        {
            var (current, own) = await zones.ForAsync(Context.Guild!.Id, Context.User.Id);
            return Replies.Ephemeral($"I read your times in {current.Id} (it's {Now(current)} there). {EventCommands.ZoneHint(current, own)}");
        }

        if (TimeZones.Find(zone) is not { } found)
            return Replies.Ephemeral($"`{zone}` isn't a time zone I know. Pick one from the list.");
        await zones.SetAsync(Context.User.Id, found.Id);
        return Replies.Ephemeral($"Your time zone is now {found.Id}; it's {Now(found)} there.");
    }

    private string Now(DateTimeZone zone) => Instant.FromDateTimeOffset(Get<TimeProvider>().GetUtcNow()).InZone(zone).ToString("HH:mm, ddd d MMM", null);
}
