using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Happenings;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;

namespace THOBOTTO.Events;

// Events, their RSVPs, and polls on when to hold them. A sweep every minute decides polls whose
// deadline passed, sends reminders and the "starting now" ping, and closes RSVPs once an event
// is well past its start. All changes run one at a time under a gate.
public sealed class EventBoard(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    Notifier notifier,
    VoicePresence presence,
    GatewayClient gateway,
    IEnumerable<IEventDecorator> decorators,
    PointsEngine points,
    HappeningForum forum,
    CalendarLinks calendars,
    TimeProvider time,
    ILogger<EventBoard> logger) : BackgroundService
{
    public static readonly HappeningKind EventHappening = new("event", "Event", "📅", "Events");
    public static readonly HappeningKind SessionHappening = new("session", "Session", "🎮", "Game sessions");
    public const string ModuleId = "events";
    public const int MaxTimeOptions = 10;
    public const int MaxSuggestions = 3;

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<Event> CreateAsync(ulong guildId, ulong channelId, ulong creatorId, string title, string? description, ulong? pingRoleId, DateTimeOffset startsAt,
        string? voiceMode, bool wantsDiscordEvent, long? gameId = null, int? capacity = null, string? mode = null, ulong? discordEventId = null, int? lengthMinutes = null)
        => AddAsync(new()
        {
            GameId = gameId,
            Capacity = capacity,
            LengthMinutes = lengthMinutes,
            Mode = mode,
            VoiceMode = voiceMode,
            WantsDiscordEvent = wantsDiscordEvent,
            DiscordEventId = discordEventId,
            GuildId = guildId,
            ChannelId = channelId,
            CreatorId = creatorId,
            Title = title,
            Description = description,
            PingRoleId = pingRoleId,
            StartsAt = startsAt,
            CreatedAt = time.GetUtcNow(),
        }, []);

    public Task<Event> CreatePollAsync(ulong guildId, ulong channelId, ulong creatorId, string title, string? description, ulong? pingRoleId,
        IReadOnlyList<DateTimeOffset> times, DateTimeOffset closesAt, bool allowProposals, string? voiceMode, bool wantsDiscordEvent, long? gameId = null, int? capacity = null, string? mode = null, int? lengthMinutes = null,
        string decide = Deciding.WhenItCloses, int? enough = null)
        => AddAsync(new()
        {
            Decide = decide,
            Enough = enough,
            GameId = gameId,
            Capacity = capacity,
            LengthMinutes = lengthMinutes,
            Mode = mode,
            VoiceMode = voiceMode,
            WantsDiscordEvent = wantsDiscordEvent,
            GuildId = guildId,
            ChannelId = channelId,
            CreatorId = creatorId,
            Title = title,
            Description = description,
            PingRoleId = pingRoleId,
            PollClosesAt = closesAt,
            AllowProposals = allowProposals,
            CreatedAt = time.GetUtcNow(),
        }, times);

    // The bot's event behind a Discord scheduled event, if there is one.
    public async Task<Event?> FindByDiscordEventAsync(ulong guildId, ulong discordEventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.GuildId == guildId && e.DiscordEventId == discordEventId && e.State == EventStates.Scheduled);
    }

    // A Discord scheduled event changed there: the bot's event follows its title, details and time; cancelled
    // there, it's cancelled here. Nothing happens when nothing differs (the bot's own changes come back this way).
    public async Task FollowAsync(Event e, string title, string? description, DateTimeOffset startsAt, DateTimeOffset? endsAt, bool cancelled)
    {
        if (cancelled)
        {
            await CancelAsync(e);
            return;
        }
        // Discord's end is ours too, unless it's only what we gave it.
        var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
        var length = Minutes(startsAt, endsAt) is { } minutes && TimeSpan.FromMinutes(minutes) != rules.LengthOf(e) ? minutes : e.LengthMinutes;
        if (e.Title == title && e.Description == description && e.StartsAt == startsAt && e.LengthMinutes == length)
            return;
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var stored = await db.Events.FindAsync(e.Id);
            if (stored is null || stored.State != EventStates.Scheduled)
                return;
            stored.Title = title;
            stored.Description = description;
            stored.StartsAt = startsAt;
            stored.LengthMinutes = length;
            stored.ReminderSent = stored.ReminderSent && startsAt <= time.GetUtcNow();
            await db.SaveChangesAsync();
            await RenderAsync(db, stored);
        }
        finally
        {
            _gate.Release();
        }
    }

    // A Discord event's length, in whole minutes, when it has an end after its start.
    private static int? Minutes(DateTimeOffset start, DateTimeOffset? end) => end is { } at && at > start ? (int)Math.Round((at - start).TotalMinutes) : null;

    public async Task<string> RsvpAsync(long eventId, ulong userId, string status)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is null || e.State != EventStates.Scheduled || e.StartsAt is null)
                return "RSVPs for this event are closed.";

            var was = (await db.EventRsvps.FindAsync(eventId, userId))?.Status;
            status = Seats.Status(status, was, (await AttendeesAsync(db, eventId)).Count, e.Capacity);
            if (status == RsvpStatuses.Waiting && was == RsvpStatuses.Waiting)
                return await WaitingTextAsync(db, e, userId);

            await SetRsvpAsync(db, eventId, userId, status);
            await db.SaveChangesAsync();
            if (status == RsvpStatuses.In)
                await points.InteractAsync(e.GuildId, userId, InteractionKind.Coming, $"event {eventId}");
            await UpdateVoiceAccessAsync(e, userId, status == RsvpStatuses.In);
            if (was == RsvpStatuses.In && status != RsvpStatuses.In)
                await MoveUpAsync(db, e);
            await RenderAsync(db, e);

            return status switch
            {
                RsvpStatuses.In => "You're in. 🎉",
                RsvpStatuses.Waiting => await WaitingTextAsync(db, e, userId),
                RsvpStatuses.Maybe => "Marked as maybe.",
                _ => "Marked as not coming.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    // Opens another session of a full one at the same time, and moves its waiting list over.
    public async Task<string> OpenAnotherAsync(long eventId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is null || e.State != EventStates.Scheduled || e.StartsAt is null || e.Capacity is not { } capacity)
                return "This can't get another session.";

            var rsvps = await db.EventRsvps.Where(r => r.EventId == e.Id).OrderBy(r => r.At).ToListAsync();
            if (rsvps.Count(r => r.Status == RsvpStatuses.In) < capacity)
                return "There's still room in this one.";

            var moving = Seats.MovingToAnother(rsvps, userId, capacity);
            db.EventRsvps.RemoveRange(rsvps.Where(r => moving.Contains(r.UserId)));

            var firstId = e.FirstPartId ?? e.Id;
            var first = e.FirstPartId is null ? e : (await db.Events.FindAsync(firstId))!;
            var number = 2 + await db.Events.CountAsync(x => x.FirstPartId == firstId);
            var inForum = e.MessageId == e.ChannelId && await forum.ForumOfAsync(e.GuildId) is not null;
            var another = await AddCoreAsync(db, new()
            {
                GuildId = e.GuildId,
                ChannelId = inForum ? (await forum.ForumOfAsync(e.GuildId))!.Value : e.ChannelId,
                CreatorId = userId,
                Title = $"{first.Title} #{number}",
                Description = e.Description,
                StartsAt = e.StartsAt,
                GameId = e.GameId,
                VoiceMode = e.VoiceMode,
                Capacity = capacity,
                LengthMinutes = e.LengthMinutes,
                Mode = e.Mode,
                FirstPartId = firstId,
                CreatedAt = time.GetUtcNow(),
            }, [], moving, moving.Count == 0 ? $"Another session of **{first.Title}**" : $"Another session of **{first.Title}**: {string.Join(' ', moving.Select(id => $"<@{id}>"))}", here: !inForum);
            await RenderAsync(db, e);

            return $"Opened **{another.Title}**: {Link(another)}" + (moving.Count == 0 ? "" : $" Moved over: {string.Join(", ", moving.Select(id => $"<@{id}>"))}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // Changes an event's limit (and whatever else goes with it). With more room, those waiting move
    // up; with less, nobody loses their spot, it just takes longer to open up. Returns how many moved up.
    public async Task<int?> ChangeLimitAsync(long eventId, Action<Event> change)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is null || e.State != EventStates.Scheduled)
                return null;

            change(e);
            await db.SaveChangesAsync();
            var moved = 0;
            while (await MoveUpAsync(db, e))
                moved++;
            await RenderAsync(db, e);
            return moved;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Its own length (none: the server's usual one); Discord's event follows. False when it isn't upcoming.
    public async Task<bool> ChangeLengthAsync(long eventId, int? minutes)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled } e)
                return false;
            e.LengthMinutes = minutes;
            await db.SaveChangesAsync();
            if (e.DiscordEventId is { } discordId && e.StartsAt is { } start)
            {
                var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
                try
                {
                    await rest.ModifyGuildScheduledEventAsync(e.GuildId, discordId, o => o.ScheduledEndTime = start + rules.LengthOf(e));
                }
                catch (RestException ex)
                {
                    logger.LogWarning("Changing the Discord event of event {EventId} in {GuildId} failed: {Message}", e.Id, e.GuildId, ex.Message);
                }
            }
            await RenderAsync(db, e);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Toggles a vote for one of a poll's times.
    public async Task<string> VoteAsync(long optionId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var option = await db.EventTimeOptions.FindAsync(optionId);
            var e = option is null ? null : await db.Events.FindAsync(option.EventId);
            if (option is null || e is null || e.State != EventStates.Scheduled || e.StartsAt is not null)
                return "Voting on this has ended.";

            var vote = await db.EventTimeVotes.FindAsync(optionId, userId);
            if (vote is null)
                db.EventTimeVotes.Add(new() { OptionId = optionId, UserId = userId });
            else
                db.EventTimeVotes.Remove(vote);
            await db.SaveChangesAsync();
            if (await SettleWhenDueAsync(db, e) is { } decided)
                return decided;
            await RenderAsync(db, e);

            var at = option.StartsAt.ToUnixTimeSeconds();
            return vote is null ? $"You can make <t:{at}:F>. 👍" : $"Vote for <t:{at}:F> taken back.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ProposeAsync(long eventId, ulong userId, DateTimeOffset startsAt)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is null || e.State != EventStates.Scheduled || e.StartsAt is not null || !e.AllowProposals)
                return "This poll doesn't take new times.";

            var options = await db.EventTimeOptions.Where(o => o.EventId == eventId).ToListAsync();
            if (options.Any(o => o.StartsAt == startsAt))
                return $"<t:{startsAt.ToUnixTimeSeconds()}:F> is already an option; vote for it instead.";
            if (options.Count >= MaxTimeOptions)
                return $"This poll already has {MaxTimeOptions} times.";

            var option = new EventTimeOption { EventId = eventId, StartsAt = startsAt, ProposedById = userId, CreatedAt = time.GetUtcNow() };
            db.EventTimeOptions.Add(option);
            await db.SaveChangesAsync();
            db.EventTimeVotes.Add(new() { OptionId = option.Id, UserId = userId });
            await db.SaveChangesAsync();
            if (await SettleWhenDueAsync(db, e) is { } decided)
                return decided;
            await RenderAsync(db, e);
            return $"Added <t:{startsAt.ToUnixTimeSeconds()}:F>, with your vote.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Another time for an event whose time is set, with the suggester's vote; it may move there at once.
    public async Task<string> SuggestAsync(long eventId, ulong userId, DateTimeOffset startsAt)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var now = time.GetUtcNow();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled, StartsAt: { } start } e || start <= now)
                return "Its time can't be changed any more.";
            if (startsAt <= now)
                return "That time has already passed.";
            if (startsAt == start)
                return "That's its time already.";
            var open = await db.EventSuggestions.Where(x => x.EventId == eventId).ToListAsync();
            if (open.Any(x => x.StartsAt == startsAt))
                return "That's suggested already: press Works for me on it.";
            if (open.Count >= MaxSuggestions)
                return $"It already has {MaxSuggestions} suggestions open.";

            var suggestion = new EventSuggestion { EventId = eventId, StartsAt = startsAt, ById = userId, CreatedAt = now };
            db.EventSuggestions.Add(suggestion);
            await db.SaveChangesAsync();
            db.EventSuggestionVotes.Add(new() { SuggestionId = suggestion.Id, UserId = userId });
            await db.SaveChangesAsync();
            if (await MovedWhenAgreedAsync(db, e, suggestion) is { } moved)
                return moved;
            await RenderAsync(db, e);
            return $"Suggested <t:{startsAt.ToUnixTimeSeconds()}:F>. {(await MovingAsync(db, e)).Rule}";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Toggles whether a suggested time works for someone; it may move the event.
    public async Task<string> WorksForAsync(long suggestionId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.EventSuggestions.FindAsync(suggestionId) is not { } suggestion
                || await db.Events.FindAsync(suggestion.EventId) is not { State: EventStates.Scheduled, StartsAt: not null } e)
                return "That suggestion is gone.";
            var vote = await db.EventSuggestionVotes.FindAsync(suggestionId, userId);
            if (vote is null)
                db.EventSuggestionVotes.Add(new() { SuggestionId = suggestionId, UserId = userId });
            else
                db.EventSuggestionVotes.Remove(vote);
            await db.SaveChangesAsync();
            if (vote is null && await MovedWhenAgreedAsync(db, e, suggestion) is { } moved)
                return moved;
            await RenderAsync(db, e);
            return vote is null ? $"Noted: <t:{suggestion.StartsAt.ToUnixTimeSeconds()}:F> works for you." : "Taken back.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Whoever planned it, or someone with events.manage (checked by the caller): a suggested time, or another.
    public async Task<string> MoveAsync(long eventId, DateTimeOffset startsAt)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var now = time.GetUtcNow();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled, StartsAt: { } start } e || start <= now)
                return "Its time can't be changed any more.";
            if (startsAt <= now)
                return "That time has already passed.";
            return startsAt == start ? "That's its time already." : await MoveCoreAsync(db, e, startsAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EventSuggestion?> SuggestionAsync(long suggestionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.EventSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == suggestionId);
    }

    // The times suggested for an event, oldest first, with who each works for.
    public async Task<IReadOnlyList<(EventSuggestion Suggestion, IReadOnlyList<ulong> WorksFor)>> SuggestionsAsync(long eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var open = await db.EventSuggestions.AsNoTracking().Where(x => x.EventId == eventId).OrderBy(x => x.Id).ToListAsync();
        var votes = await db.EventSuggestionVotes.AsNoTracking().Where(v => open.Select(x => x.Id).Contains(v.SuggestionId)).ToListAsync();
        return [.. open.Select(x => (x, (IReadOnlyList<ulong>)[.. votes.Where(v => v.SuggestionId == x.Id).Select(v => v.UserId)]))];
    }

    // How a suggestion moves the event, as said on its card; and how many of those who are in it takes.
    private async Task<(string Rule, int Needed)> MovingAsync(BotDbContext db, Event e)
    {
        var going = (await AttendeesAsync(db, e.Id)).Count;
        return (await settings.GetAsync<EventRules>(e.GuildId, ModuleId)).Move == Moving.Majority
            ? ($"It moves when {Moving.Needed(going)} of the {going} who are in say it works.", Moving.Needed(going))
            : ($"<@{e.CreatorId}> moves it.", 0);
    }

    private async Task<string?> MovedWhenAgreedAsync(BotDbContext db, Event e, EventSuggestion suggestion)
    {
        if ((await settings.GetAsync<EventRules>(e.GuildId, ModuleId)).Move != Moving.Majority)
            return null;
        var going = await AttendeesAsync(db, e.Id);
        var agreeing = await db.EventSuggestionVotes.Where(v => v.SuggestionId == suggestion.Id).Select(v => v.UserId).ToListAsync();
        return Moving.Agreed(going.Count, agreeing.Count(going.Contains)) ? await MoveCoreAsync(db, e, suggestion.StartsAt) : null;
    }

    // The new time: reminders and the start ping happen again, a Discord event follows, an empty voice channel goes
    // (it opens again before the new start), and everyone who answered is told.
    private async Task<string> MoveCoreAsync(BotDbContext db, Event e, DateTimeOffset startsAt)
    {
        var was = e.StartsAt!.Value;
        (e.StartsAt, e.ReminderSent, e.StartSent) = (startsAt, false, false);
        await db.EventSuggestions.Where(x => x.EventId == e.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        if (e.VoiceChannelId is { } voice && !presence.Snapshot(e.GuildId).Values.Any(p => p.ChannelId == voice))
            await CloseVoiceAsync(db, e);
        if (e.DiscordEventId is { } discordId)
        {
            var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
            try
            {
                await rest.ModifyGuildScheduledEventAsync(e.GuildId, discordId, o => (o.ScheduledStartTime, o.ScheduledEndTime) = (startsAt, startsAt + rules.LengthOf(e)));
            }
            catch (RestException ex)
            {
                logger.LogWarning("Moving the Discord event of event {EventId} in {GuildId} failed: {Message}", e.Id, e.GuildId, ex.Message);
            }
        }
        await RenderAsync(db, e);

        var answered = await db.EventRsvps.Where(r => r.EventId == e.Id && r.Status != RsvpStatuses.Out).Select(r => r.UserId).ToListAsync();
        var at = startsAt.ToUnixTimeSeconds();
        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, answered, $"**{e.Title}** moved to <t:{at}:F>", Link(e));
        await PostAsync(e, $"**{e.Title}** moved to <t:{at}:F> (<t:{at}:R>), from <t:{was.ToUnixTimeSeconds()}:F>. {string.Join(' ', answered.Select(id => $"<@{id}>"))}", answered);
        return $"Moved to <t:{at}:F>.";
    }

    // Before the start: the times suggested (and how they move it) on the card, a row each to agree or move, and one
    // to suggest another.
    private async Task<IReadOnlyList<ActionRowProperties>> SuggestionRowsAsync(BotDbContext db, Event e, StringBuilder text, bool before)
    {
        if (!before)
            return [];
        var open = await db.EventSuggestions.Where(x => x.EventId == e.Id).OrderBy(x => x.Id).ToListAsync();
        var votes = await db.EventSuggestionVotes.Where(v => open.Select(x => x.Id).Contains(v.SuggestionId)).ToListAsync();
        if (open.Count > 0)
        {
            text.AppendLine();
            for (var i = 0; i < open.Count; i++)
            {
                var works = votes.Where(v => v.SuggestionId == open[i].Id).Select(v => $"<@{v.UserId}>").ToList();
                text.AppendLine($"💡 **{i + 1}.** <t:{open[i].StartsAt.ToUnixTimeSeconds()}:F>, suggested by <@{open[i].ById}> · works for {(works.Count == 0 ? "nobody yet" : string.Join(", ", works))}");
            }
            text.AppendLine($"-# {(await MovingAsync(db, e)).Rule}");
        }
        List<ActionRowProperties> rows = [.. open.Select((x, i) => new ActionRowProperties
        {
            new ButtonProperties($"eventworks:{x.Id}", $"{i + 1} works for me", EmojiProperties.Standard("👍"), ButtonStyle.Secondary),
            new ButtonProperties($"eventmoveto:{x.Id}", $"Move to {i + 1}", ButtonStyle.Secondary),
        })];
        if (open.Count < MaxSuggestions)
            rows.Insert(0, new ActionRowProperties { new ButtonProperties($"eventsuggest:{e.Id}", "Suggest another time", EmojiProperties.Standard("💡"), ButtonStyle.Secondary) });
        return rows;
    }

    // How many it takes to settle the poll as soon as a time has enough: its own number, its limit, else the server's.
    public async Task<int> EnoughAsync(Event e) => e.Enough ?? e.Capacity ?? (await settings.GetAsync<EventRules>(e.GuildId, ModuleId)).EnoughPeople;

    // Under "as soon as a time has enough people", settles the poll once one has; what it says, when it did.
    private async Task<string?> SettleWhenDueAsync(BotDbContext db, Event e)
    {
        if (e.Decide != Deciding.AsSoonAsEnough)
            return null;
        var options = await db.EventTimeOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.Id).ToListAsync();
        var votes = await db.EventTimeVotes.Where(v => options.Select(o => o.Id).Contains(v.OptionId)).ToListAsync();
        var counts = options.Select(o => votes.Count(v => v.OptionId == o.Id)).ToList();
        return Deciding.Settled(e.Decide, [.. options.Select(o => o.StartsAt)], counts, await EnoughAsync(e), time.GetUtcNow(), closed: false) is { } chosen
            ? await DecideCoreAsync(db, e, options[chosen].Id)
            : null;
    }

    // Settles a poll: the given option, or else the one with most votes.
    public async Task<string> DecideAsync(long eventId, long? optionId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = (await db.Events.FindAsync(eventId))!;
            return await DecideCoreAsync(db, e, optionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync(Event e)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Events.Attach(e);
            e.State = EventStates.Cancelled;
            await db.SaveChangesAsync();
            await RenderAsync(db, e);
            await CloseExtrasAsync(db, e, cancelled: true);

            var involved = (await AttendeesAsync(db, e.Id)).Concat(await VotersAsync(db, e.Id)).Distinct();
            await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, involved, $"**{e.Title}** was cancelled", Link(e));
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Sweep, time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping events failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<EventSeries> CreateSeriesAsync(EventSeries series)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.EventSeries.Add(series);
            await db.SaveChangesAsync();
        }
        finally
        {
            _gate.Release();
        }

        // Open what falls in the window straight away rather than at the next sweep.
        await OpenOccurrencesAsync(CancellationToken.None);
        return series;
    }

    public async Task StopSeriesAsync(long seriesId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.EventSeries.Where(s => s.Id == seriesId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false));
    }

    private async Task OpenOccurrencesAsync(CancellationToken ct)
    {
        List<EventSeries> active;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            active = await db.EventSeries.Where(s => s.Active).ToListAsync(ct);

        var now = Instant.FromDateTimeOffset(time.GetUtcNow());
        foreach (var series in active)
        {
            if (TimeZones.Find(series.Zone) is not { } zone)
                continue;

            var clock = LocalTime.FromMinutesSinceMidnight(series.TimeOfDay);
            foreach (var at in Recurrence.Upcoming(series.Days, clock, zone, now, Duration.FromDays(series.OpenDaysAhead)))
            {
                var start = at.ToDateTimeOffset();
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                if (await db.Events.AnyAsync(e => e.SeriesId == series.Id && e.StartsAt == start, ct))
                    continue;

                await AddAsync(new()
                {
                    GuildId = series.GuildId,
                    ChannelId = series.ChannelId,
                    CreatorId = series.CreatorId,
                    Title = series.Title,
                    Description = series.Description,
                    PingRoleId = series.PingRoleId,
                    StartsAt = start,
                    SeriesId = series.Id,
                    VoiceMode = series.VoiceMode,
                    WantsDiscordEvent = series.WantsDiscordEvent,
                    Capacity = series.Capacity,
                    LengthMinutes = series.LengthMinutes,
                    GameId = series.GameId,
                    Mode = series.Mode,
                    CreatedAt = time.GetUtcNow(),
                }, []);
            }
        }
    }

    private async Task<Event> AddAsync(Event e, IReadOnlyList<DateTimeOffset> times)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            // The creator of a one-off is going, presumably; a series doesn't sign them up every time.
            var going = e.StartsAt is not null && e.SeriesId is null ? new[] { e.CreatorId } : [];
            await AddCoreAsync(db, e, times, going, e.PingRoleId is { } role ? $"<@&{role}>" : null, here: e.ChannelId != await forum.ForumOfAsync(e.GuildId));

            var when = e.StartsAt is { } s ? $"<t:{s.ToUnixTimeSeconds()}:F>" : "time to be voted on";
            await notifier.NotifySubscribersAsync(e.GuildId, NotificationTopics.EventsNew, $"new event **{e.Title}**, {when}", Link(e));
            foreach (var decorator in decorators)
                await decorator.PostedAsync(e);
            return e;
        }
        finally
        {
            _gate.Release();
        }
    }

    // One made in Discord's calendar, when the forum shows events: it lives in its forum post.
    public async Task<Event> TakeInAsync(ulong guildId, ulong forumId, ulong creatorId, string title, string? description, DateTimeOffset startsAt, DateTimeOffset? endsAt, ulong discordEventId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = new Event
            {
                WantsDiscordEvent = true,
                DiscordEventId = discordEventId,
                GuildId = guildId,
                ChannelId = forumId,
                CreatorId = creatorId,
                Title = title,
                Description = description,
                StartsAt = startsAt,
                LengthMinutes = Minutes(startsAt, endsAt),
                CreatedAt = time.GetUtcNow(),
            };
            return await AddCoreAsync(db, e, [], [], null, here: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Saves and posts an event (where it was planned, unless it was taken in from Discord's calendar, and in the
    // forum); the content pings its role, or mentions those already in.
    private async Task<Event> AddCoreAsync(BotDbContext db, Event e, IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ulong> going, string? content, bool here = true)
    {
        db.Events.Add(e);
        await db.SaveChangesAsync();

        foreach (var userId in going)
            db.EventRsvps.Add(new() { EventId = e.Id, UserId = userId, Status = RsvpStatuses.In, At = e.CreatedAt });
        foreach (var at in times.Distinct().Order())
            db.EventTimeOptions.Add(new() { EventId = e.Id, StartsAt = at, ProposedById = e.CreatorId, CreatedAt = e.CreatedAt });
        await db.SaveChangesAsync();

        var (embed, components) = await BuildAsync(db, e);
        if (here)
        {
            var message = await rest.SendMessageAsync(e.ChannelId, new()
            {
                Content = content,
                Embeds = [embed],
                Components = components,
                AllowedMentions = new() { AllowedRoles = e.PingRoleId is { } r ? [r] : [], AllowedUsers = going.ToList() },
            });
            e.MessageId = message.Id;
            await db.SaveChangesAsync();
        }
        var mentions = new AllowedMentionsProperties { AllowedRoles = e.PingRoleId is { } pinged ? [pinged] : [], AllowedUsers = going.ToList() };
        var shown = await forum.ShowAsync(e.GuildId, Kind(e), e.Id, HappeningStates.Open, here ? new(e.Title, [embed], components) : new(e.Title, [embed], components, Content: content, Mentions: mentions));
        if (shown is { } post && !here)
        {
            (e.ChannelId, e.MessageId) = (post, post);
            await db.SaveChangesAsync();
        }
        else if (!here)
            logger.LogWarning("Event {EventId} of {GuildId} belongs in the forum, which doesn't show it: it has no card", e.Id, e.GuildId);
        await EnsureDiscordEventAsync(db, e);
        return e;
    }

    private async Task<string> DecideCoreAsync(BotDbContext db, Event e, long? optionId)
    {
        var options = await db.EventTimeOptions.Where(o => o.EventId == e.Id).ToListAsync();
        var votes = await db.EventTimeVotes.Where(v => options.Select(o => o.Id).Contains(v.OptionId)).ToListAsync();
        var now = time.GetUtcNow();

        var winner = optionId is { } chosen ? options.FirstOrDefault(o => o.Id == chosen && o.StartsAt > now) : PollMath.Winner(options, votes, now);
        if (winner is null)
        {
            if (optionId is not null)
                return "That time isn't an option, or has passed.";
            e.State = EventStates.Cancelled;
            await db.SaveChangesAsync();
            await RenderAsync(db, e);
            return "Nobody voted for a time still ahead, so it's cancelled.";
        }

        e.StartsAt = winner.StartsAt;
        var voted = votes.Where(v => v.OptionId == winner.Id).Select(v => v.UserId).ToList();
        var (going, waiting) = Seats.Split(voted, e.Capacity);
        foreach (var userId in voted)
            await SetRsvpAsync(db, e.Id, userId, going.Contains(userId) ? RsvpStatuses.In : RsvpStatuses.Waiting);
        await db.SaveChangesAsync();
        await RenderAsync(db, e);
        await EnsureDiscordEventAsync(db, e);

        var at = winner.StartsAt.ToUnixTimeSeconds();
        var everyone = votes.Select(v => v.UserId).Distinct().ToList();
        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, everyone, $"**{e.Title}** will be <t:{at}:F>", Link(e));
        await PostAsync(e, $"**{e.Title}** is on: <t:{at}:F> (<t:{at}:R>). Marked as in: {(going.Count == 0 ? "nobody yet" : string.Join(' ', going.Select(id => $"<@{id}>")))}."
            + (waiting.Count == 0 ? "" : $" It's full; waiting: {string.Join(' ', waiting.Select(id => $"<@{id}>"))}."), voted);
        return $"Decided: <t:{at}:F>.";
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await OpenOccurrencesAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            foreach (var poll in await db.Events.Where(e => e.State == EventStates.Scheduled && e.StartsAt == null && e.Decide != Deciding.PlannerDecides && e.PollClosesAt <= now).ToListAsync(ct))
                await DecideCoreAsync(db, poll, null);
            // One its planner settles has no deadline; once every time has passed, there's nothing left to settle.
            foreach (var poll in await db.Events.Where(e => e.State == EventStates.Scheduled && e.StartsAt == null && e.Decide == Deciding.PlannerDecides
                && !db.EventTimeOptions.Any(o => o.EventId == e.Id && o.StartsAt > now)).ToListAsync(ct))
                await DecideCoreAsync(db, poll, null);

            var due = await db.Events.Where(e => e.State == EventStates.Scheduled && e.StartsAt != null && e.StartsAt <= now + TimeSpan.FromDays(1)).ToListAsync(ct);
            foreach (var e in due)
            {
                var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
                var start = e.StartsAt!.Value;

                if (!e.ReminderSent && rules.ReminderMinutes > 0 && start - now <= TimeSpan.FromMinutes(rules.ReminderMinutes) && start > now)
                {
                    e.ReminderSent = true;
                    await db.SaveChangesAsync(ct);
                    await AnnounceAsync(db, e, $"**{e.Title}** starts <t:{start.ToUnixTimeSeconds()}:R>.");
                    var minutes = (int)Math.Ceiling((start - now).TotalMinutes);
                    foreach (var decorator in decorators)
                        await decorator.AnnouncedAsync(e, $"{e.Title} starts in {minutes} minute{(minutes == 1 ? "" : "s")}.");
                }

                if (e.VoiceMode is not null && e.VoiceChannelId is null && !e.StartSent && start - now <= TimeSpan.FromMinutes(rules.VoiceLeadMinutes))
                    await OpenVoiceAsync(db, e, rules);

                // After the start, an empty voice channel has done its job.
                if (e.VoiceChannelId is { } voice && now - start >= TimeSpan.FromMinutes(15) && !presence.Snapshot(e.GuildId).Values.Any(p => p.ChannelId == voice))
                    await CloseVoiceAsync(db, e);

                if (!e.StartSent && start <= now)
                {
                    e.StartSent = true;
                    e.ReminderSent = true;
                    await db.SaveChangesAsync(ct);
                    await AnnounceAsync(db, e, $"**{e.Title}** is starting now!");
                    foreach (var decorator in decorators)
                        await decorator.AnnouncedAsync(e, $"{e.Title} is starting now!");
                }

                if (start + TimeSpan.FromHours(rules.EndAfterHours) <= now)
                {
                    e.State = EventStates.Over;
                    await db.SaveChangesAsync(ct);
                    await RenderAsync(db, e);
                    await CloseExtrasAsync(db, e, cancelled: false);
                    continue;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Pings those who are in, in the event's channel, and DMs those who asked for reminders.
    private async Task AnnounceAsync(BotDbContext db, Event e, string text)
    {
        var going = await AttendeesAsync(db, e.Id);
        if (going.Count > 0)
            await PostAsync(e, $"{text} {string.Join(' ', going.Select(id => $"<@{id}>"))}", going);
        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, going, text, Link(e));
    }

    private async Task PostAsync(Event e, string content, IReadOnlyList<ulong> mention)
    {
        try
        {
            await rest.SendMessageAsync(e.ChannelId, new()
            {
                Content = content,
                MessageReference = e.MessageId is { } m ? MessageReferenceProperties.Reply(m, failIfNotExists: false) : null,
                AllowedMentions = new() { AllowedUsers = mention, ReplyMention = false },
            });
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            logger.LogWarning("Couldn't post about event {EventId} in {GuildId}: {Message}", e.Id, e.GuildId, ex.Message);
        }
    }

    private async Task SetRsvpAsync(BotDbContext db, long eventId, ulong userId, string status)
    {
        var rsvp = await db.EventRsvps.FindAsync(eventId, userId);
        if (rsvp is null)
            db.EventRsvps.Add(new() { EventId = eventId, UserId = userId, Status = status, At = time.GetUtcNow() });
        else
        {
            rsvp.Status = status;
            rsvp.At = time.GetUtcNow();
        }
    }

    // Someone left a full event: the first one waiting gets their spot.
    private async Task<bool> MoveUpAsync(BotDbContext db, Event e)
    {
        var next = await db.EventRsvps.Where(r => r.EventId == e.Id && r.Status == RsvpStatuses.Waiting).OrderBy(r => r.At).FirstOrDefaultAsync();
        if (next is null || !Seats.HasRoom((await AttendeesAsync(db, e.Id)).Count, e.Capacity))
            return false;

        next.Status = RsvpStatuses.In;
        await db.SaveChangesAsync();
        await UpdateVoiceAccessAsync(e, next.UserId, true);
        await PostAsync(e, $"A spot opened in **{e.Title}**: <@{next.UserId}>, you're in.", [next.UserId]);
        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, [next.UserId], $"a spot opened in **{e.Title}**: you're in", Link(e));
        return true;
    }

    private static async Task<string> WaitingTextAsync(BotDbContext db, Event e, ulong userId)
    {
        var waiting = await db.EventRsvps.Where(r => r.EventId == e.Id && r.Status == RsvpStatuses.Waiting).OrderBy(r => r.At).Select(r => r.UserId).ToListAsync();
        return $"It's full ({e.Capacity}/{e.Capacity}). You're #{waiting.IndexOf(userId) + 1} on the waiting list: you move up when someone drops out, or ➕ Another session opens one more at the same time.";
    }

    private static async Task<List<ulong>> AttendeesAsync(BotDbContext db, long eventId)
        => await db.EventRsvps.Where(r => r.EventId == eventId && r.Status == RsvpStatuses.In).Select(r => r.UserId).ToListAsync();

    private static async Task<List<ulong>> VotersAsync(BotDbContext db, long eventId)
        => await db.EventTimeVotes.Where(v => db.EventTimeOptions.Any(o => o.Id == v.OptionId && o.EventId == eventId)).Select(v => v.UserId).Distinct().ToListAsync();

    // Mirrors an event that has a time as a Discord scheduled event, once.
    private async Task EnsureDiscordEventAsync(BotDbContext db, Event e)
    {
        if (!e.WantsDiscordEvent || e.DiscordEventId is not null || e.StartsAt is not { } start)
            return;

        var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
        try
        {
            var created = await rest.CreateGuildScheduledEventAsync(e.GuildId,
                new(e.Title, GuildScheduledEventPrivacyLevel.GuildOnly, start, GuildScheduledEventEntityType.External)
                {
                    // A voice event needs its channel up front; ours opens shortly before the start.
                    ScheduledEndTime = start + rules.LengthOf(e),
                    Description = e.Description,
                    Metadata = new(e.MessageId is { } m ? $"https://discord.com/channels/{e.GuildId}/{e.ChannelId}/{m}" : "On this server"),
                });
            e.DiscordEventId = created.Id;
            await db.SaveChangesAsync();
        }
        catch (RestException ex)
        {
            logger.LogWarning(ex, "Creating a Discord event for event {EventId} in {GuildId} failed", e.Id, e.GuildId);
        }
    }

    // The category of the channel a card is in; a forum post's is its forum's.
    private async Task<ulong?> CategoryOfAsync(ulong channelId)
    {
        var channel = await rest.GetChannelAsync(channelId);
        if (channel is GuildThread { ParentId: { } parent })
            channel = await rest.GetChannelAsync(parent);
        return channel switch { TextGuildChannel text => text.ParentId, ForumGuildChannel posts => posts.ParentId, _ => null };
    }

    private async Task OpenVoiceAsync(BotDbContext db, Event e, EventRules rules)
    {
        try
        {
            var category = rules.VoiceCategoryId ?? await CategoryOfAsync(e.ChannelId);
            var overwrites = new List<PermissionOverwriteProperties>();
            if (e.VoiceMode == VoiceModes.Locked)
            {
                // Everyone sees it; only those who are In may join. The bot keeps access to manage it.
                overwrites.Add(new(e.GuildId, PermissionOverwriteType.Role) { Denied = Permissions.Connect });
                overwrites.Add(new(gateway.Cache.User!.Id, PermissionOverwriteType.User) { Allowed = Permissions.Connect | Permissions.ManageChannels | Permissions.ViewChannel });
                foreach (var userId in await AttendeesAsync(db, e.Id))
                    overwrites.Add(new(userId, PermissionOverwriteType.User) { Allowed = Permissions.Connect });
            }

            var channel = await rest.CreateGuildChannelAsync(e.GuildId, new(e.Title.Length <= 100 ? e.Title : e.Title[..100], ChannelType.VoiceGuildChannel)
            {
                ParentId = category,
                PermissionOverwrites = overwrites,
            });
            e.VoiceChannelId = channel.Id;
            await db.SaveChangesAsync();

            var who = e.VoiceMode == VoiceModes.Locked ? " (for those who are in)" : "";
            await PostAsync(e, $"🔊 Voice for **{e.Title}** is open{who}: <#{channel.Id}>", []);
        }
        catch (RestException ex)
        {
            logger.LogWarning(ex, "Opening voice for event {EventId} in {GuildId} failed", e.Id, e.GuildId);
        }
    }

    private async Task UpdateVoiceAccessAsync(Event e, ulong userId, bool going)
    {
        if (e.VoiceMode != VoiceModes.Locked || e.VoiceChannelId is not { } channelId)
            return;
        try
        {
            if (going)
                await rest.ModifyGuildChannelPermissionsAsync(channelId, new(userId, PermissionOverwriteType.User) { Allowed = Permissions.Connect });
            else
                await rest.DeleteGuildChannelPermissionAsync(channelId, userId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private async Task CloseVoiceAsync(BotDbContext db, Event e)
    {
        if (e.VoiceChannelId is not { } channelId)
            return;
        try
        {
            await rest.DeleteChannelAsync(channelId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
        e.VoiceChannelId = null;
        await db.SaveChangesAsync();
    }

    // When an event ends or is cancelled: its voice channel goes, and a cancelled one's Discord event too.
    private async Task CloseExtrasAsync(BotDbContext db, Event e, bool cancelled)
    {
        await CloseVoiceAsync(db, e);
        if (cancelled && e.DiscordEventId is { } discordEvent)
        {
            try
            {
                await rest.DeleteGuildScheduledEventAsync(e.GuildId, discordEvent);
            }
            catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
    }

    // Every copy of the card: where it was planned, and in the forum (one taken in from Discord's calendar has only that).
    private async Task RenderAsync(BotDbContext db, Event e)
    {
        var (embed, components) = await BuildAsync(db, e);
        var state = e.State switch { EventStates.Over => HappeningStates.Done, EventStates.Cancelled => HappeningStates.Cancelled, _ => HappeningStates.Open };
        await forum.ShowAsync(e.GuildId, Kind(e), e.Id, state, new(e.Title, [embed], components));
        if (e.MessageId is not { } messageId || messageId == e.ChannelId)
            return;
        try
        {
            await rest.ModifyMessageAsync(e.ChannelId, messageId, m =>
            {
                m.Embeds = [embed];
                m.Components = components;
            });
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<Event?> FindAsync(ulong guildId, long eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId);
    }

    // Re-renders an event's message, e.g. when what a decorator shows has changed.
    public async Task RefreshAsync(long eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Events.FindAsync(eventId) is { } e)
            await RenderAsync(db, e);
    }

    private async Task<(EmbedProperties Embed, IEnumerable<IMessageComponentProperties> Components)> BuildAsync(BotDbContext db, Event e)
    {
        var text = new StringBuilder();
        var open = e.State == EventStates.Scheduled;
        IEnumerable<IMessageComponentProperties> components;

        if (e.StartsAt is null && e.State != EventStates.Cancelled)
        {
            var options = await db.EventTimeOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.StartsAt).ToListAsync();
            var ids = options.Select(o => o.Id).ToList();
            var votes = await db.EventTimeVotes.Where(v => ids.Contains(v.OptionId)).ToListAsync();

            text.AppendLine($"**When can you make it?** Vote for every time that works. {Deciding.Explain(e.Decide, await EnoughAsync(e), e.PollClosesAt!.Value, $"<@{e.CreatorId}>")}");
            if (e.Description is { } description)
                text.AppendLine().AppendLine(description);
            text.AppendLine();
            for (var i = 0; i < options.Count; i++)
            {
                var who = votes.Where(v => v.OptionId == options[i].Id).Select(v => $"<@{v.UserId}>").ToList();
                text.AppendLine($"**{i + 1}.** <t:{options[i].StartsAt.ToUnixTimeSeconds()}:F> · {who.Count} {(who.Count == 1 ? "vote" : "votes")}{(who.Count > 0 ? $": {string.Join(", ", who)}" : "")}");
            }

            var rows = options.Select((o, i) => new ButtonProperties($"eventvote:{o.Id}", $"{i + 1}", ButtonStyle.Primary) { Disabled = !open })
                .Chunk(5)
                .Select(chunk => (IMessageComponentProperties)new ActionRowProperties(chunk))
                .ToList();
            var extras = new ActionRowProperties();
            if (e.AllowProposals && open && options.Count < MaxTimeOptions)
                extras.Add(new ButtonProperties($"eventpropose:{e.Id}", "Propose a time", EmojiProperties.Standard("➕"), ButtonStyle.Secondary));
            if (open)
                extras.Add(new ButtonProperties($"eventsettle:{e.Id}", "Settle now", ButtonStyle.Secondary));
            if (extras.Any())
                rows.Add(extras);
            components = rows;
        }
        else
        {
            var rsvps = await db.EventRsvps.Where(r => r.EventId == e.Id).OrderBy(r => r.At).ToListAsync();
            string Names(string status)
            {
                var who = rsvps.Where(r => r.Status == status).Select(r => $"<@{r.UserId}>").ToList();
                return who.Count == 0 ? "–" : string.Join(", ", who);
            }

            var start = e.StartsAt?.ToUnixTimeSeconds();
            text.AppendLine(e.State switch
            {
                EventStates.Cancelled => start is null ? "**Cancelled.**" : $"~~<t:{start}:F>~~ **Cancelled.**",
                EventStates.Over => $"<t:{start}:F> (over)",
                _ => $"<t:{start}:F> (<t:{start}:R>)",
            } + (e.LengthMinutes is { } minutes && e.State != EventStates.Cancelled ? $" · {Durations.Format(TimeSpan.FromMinutes(minutes))}" : ""));
            if (e.Description is { } description)
                text.AppendLine().AppendLine(description);
            text.AppendLine();
            var going = rsvps.Count(r => r.Status == RsvpStatuses.In);
            var full = going >= e.Capacity;
            text.AppendLine($"✅ **In** ({going}{(e.Capacity is { } capacity ? $"/{capacity}" : "")}{(full ? ", full" : "")}): {Names(RsvpStatuses.In)}");
            if (rsvps.Any(r => r.Status == RsvpStatuses.Waiting))
                text.AppendLine($"⏳ **Waiting**: {Names(RsvpStatuses.Waiting)}");
            text.AppendLine($"🤔 **Maybe**: {Names(RsvpStatuses.Maybe)}");
            text.AppendLine($"❌ **Out**: {Names(RsvpStatuses.Out)}");

            var row = new ActionRowProperties
            {
                new ButtonProperties($"event:{e.Id}:{RsvpStatuses.In}", full ? "Wait for a spot" : "In", EmojiProperties.Standard(full ? "⏳" : "✅"), ButtonStyle.Success) { Disabled = !open },
                new ButtonProperties($"event:{e.Id}:{RsvpStatuses.Maybe}", "Maybe", EmojiProperties.Standard("🤔"), ButtonStyle.Secondary) { Disabled = !open },
                new ButtonProperties($"event:{e.Id}:{RsvpStatuses.Out}", "Out", EmojiProperties.Standard("❌"), ButtonStyle.Secondary) { Disabled = !open },
            };
            if (full && open)
                row.Add(new ButtonProperties($"eventanother:{e.Id}", "Another session", EmojiProperties.Standard("➕"), ButtonStyle.Primary));
            if (open && await calendars.AddPageAsync(e.GuildId, e.Id) is { } add)
                row.Add(new LinkButtonProperties(add, "Add to calendar", EmojiProperties.Standard("📅")));
            components = [row, .. await SuggestionRowsAsync(db, e, text, open && e.StartsAt > time.GetUtcNow())];
        }

        foreach (var decorator in decorators)
            foreach (var line in await decorator.LinesAsync(e))
                text.AppendLine(line);
        text.Append($"-# Event {e.Id} by <@{e.CreatorId}> · times show in your own time zone");
        var embed = new EmbedProperties
        {
            Title = e.Title,
            Description = text.ToString(),
            Color = open ? new(0x5865F2) : new(0x99AAB5),
        };
        return (embed, components);
    }

    private static string Kind(Event e) => e.GameId is null ? EventHappening.Id : SessionHappening.Id;

    private static string? Link(Event e) => e.MessageId is { } m ? Notifier.Link(e.GuildId, e.ChannelId, m) : null;
}
