using NetCord;
using NetCord.Gateway;

using NodaTime;

namespace THOBOTTO.Events;

// Something the events page offers to plan besides a plain event, as the form shows it and sends it back.
public sealed record EventChoice(string Value, string Label);

// A kind of event another module adds (a game session): what the events page offers of it, what its events are
// for, and planning one from a choice.
public interface IEventKind
{
    Task<IReadOnlyList<EventChoice>> ChoicesAsync(ulong guildId);

    // What events and series with a GameId are for, by that id, as the page names them.
    Task<IReadOnlyDictionary<long, string>> NamesAsync(ulong guildId);

    // An event of the chosen kind at the times (a vote when several); or why not.
    Task<(Event? Event, string? Problem)> PlanAsync(Guild guild, GuildUser user, string choice, IReadOnlyList<DateTimeOffset> times, string title,
        EventVoice voice, bool pingRole, int? limit, ulong channelId);

    // A series of the chosen kind on those weekdays; or why not.
    Task<(EventSeries? Series, string? Problem)> PlanSeriesAsync(Guild guild, GuildUser user, string choice, int[] days, LocalTime timeOfDay, DateTimeZone zone,
        string title, EventVoice voice, bool pingRole, int? limit, ulong channelId, int openDaysAhead);
}
