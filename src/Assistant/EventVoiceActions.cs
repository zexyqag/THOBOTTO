using Microsoft.EntityFrameworkCore;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Events;

namespace THOBOTTO.Assistant;

// "I'm in for Friday's raid", "put me down as maybe": an RSVP to an upcoming event. Only the speaker's own
// answer changes, so it's asked about only when it isn't clear which event.
public sealed class EventVoiceActions(EventBoard board, TimeZones zones, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time) : IVoiceActions
{
    private const double Alike = 0.6;
    private const int MostAsked = 3;
    // Without a title said, only an event this soon counts as the obvious one.
    private static readonly TimeSpan Soon = TimeSpan.FromDays(2);

    public IEnumerable<VoiceAction> Actions =>
    [
        new("rsvp", "Answer an upcoming event: in (coming), maybe, or out (not coming).",
            new() { ["answer"] = Schema.OneOf("Coming or not", RsvpStatuses.In, RsvpStatuses.Maybe, RsvpStatuses.Out), ["event"] = Schema.Text("Which event, if said") }, ["answer"],
            RsvpAsync,
            ("count me in for friday's raid", """{"answer":"in","event":"friday raid"}""")),
    ];

    private async Task<VoicePlan> RsvpAsync(VoiceRequest request, System.Text.Json.JsonElement args)
    {
        var heard = request.Heard;
        var answer = Schema.String(args, "answer") ?? RsvpStatuses.In;
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var upcoming = await db.Events.AsNoTracking()
            .Where(e => e.GuildId == heard.GuildId && e.State == EventStates.Scheduled && e.StartsAt != null && e.StartsAt > now)
            .OrderBy(e => e.StartsAt).Take(20).ToListAsync();
        var (zone, _) = await zones.ForAsync(heard.GuildId, heard.UserId);
        string When(Event e) => Instant.FromDateTimeOffset(e.StartsAt!.Value).InZone(zone).ToString("ddd HH:mm", null);

        var named = Schema.String(args, "event");
        var fits = named is null
            ? upcoming.Where(e => e.StartsAt - now < Soon).ToList()
            : upcoming.Select(e => (Event: e, Score: Likeness.Of(named, $"{e.Title} {When(e)}"))).Where(f => f.Score >= Alike).OrderByDescending(f => f.Score).Select(f => f.Event).ToList();
        if (fits.Count == 0)
            return VoicePlan.Done(upcoming.Count == 0 ? "There's no upcoming event." : named is null ? "Which event? None is coming up in the next two days." : $"I can't find an event like “{named}”.");

        VoiceChoice Answer(Event e, string label) => new(label, async () => $"**{e.Title}**: {await board.RsvpAsync(e.Id, heard.UserId, answer)}");
        if (fits is [var only])
            return VoicePlan.Done(await Answer(only, "").RunAsync());
        return VoicePlan.Ask("Which event?", [.. fits.Take(MostAsked).Select(e => Answer(e, $"{e.Title} · {When(e)}"))]);
    }
}
