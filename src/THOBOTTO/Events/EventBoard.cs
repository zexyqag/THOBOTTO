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
    public const int MaxGames = 5;

    // How long a vote on the time is open unless its planner changes it.
    public static readonly TimeSpan VoteOpen = TimeSpan.FromHours(24);

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    // The least a vote on the game gets once the time is set.
    private static readonly TimeSpan GameAtLeast = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<Event> CreateAsync(ulong guildId, ulong channelId, ulong creatorId, string title, string? description, ulong? pingRoleId, DateTimeOffset startsAt,
        string? voiceMode, bool wantsDiscordEvent, long? gameId = null, int? capacity = null, string? mode = null, ulong? discordEventId = null, int? lengthMinutes = null,
        string? move = null, IReadOnlyList<EventGameOption>? games = null, string decide = Deciding.WhenItCloses, int? enough = null, string suggestions = Suggesting.TimesAndGames)
        => AddAsync(new()
        {
            Suggestions = suggestions,
            Move = move,
            ChoosingGame = games is { Count: > 1 },
            Decide = decide,
            Enough = enough,
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
        }, [], games);

    public Task<Event> CreatePollAsync(ulong guildId, ulong channelId, ulong creatorId, string title, string? description, ulong? pingRoleId,
        IReadOnlyList<DateTimeOffset> times, DateTimeOffset closesAt, string suggestions, string? voiceMode, bool wantsDiscordEvent, long? gameId = null, int? capacity = null, string? mode = null, int? lengthMinutes = null,
        string decide = Deciding.WhenItCloses, int? enough = null, IReadOnlyList<EventGameOption>? games = null, string? move = null)
        => AddAsync(new()
        {
            Move = move,
            Decide = decide,
            Enough = enough,
            ChoosingGame = games is { Count: > 1 },
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
            Suggestions = suggestions,
            CreatedAt = time.GetUtcNow(),
        }, times, games);

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
            var said = await RsvpCoreAsync(db, e, userId, status);
            await RenderAsync(db, e);
            return said;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate, for an event whose time is set; the card isn't rendered. Out takes back their game votes.
    private async Task<string> RsvpCoreAsync(BotDbContext db, Event e, ulong userId, string status)
    {
        var was = (await db.EventRsvps.FindAsync(e.Id, userId))?.Status;
        status = Seats.Status(status, was, (await AttendeesAsync(db, e.Id)).Count, e.Capacity);
        if (status == RsvpStatuses.Waiting && was == RsvpStatuses.Waiting)
            return await WaitingTextAsync(db, e, userId);

        await SetRsvpAsync(db, e.Id, userId, status);
        if (status == RsvpStatuses.Out)
            await db.EventGameVotes.Where(v => v.EventId == e.Id && v.UserId == userId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        if (status == RsvpStatuses.In)
            await points.InteractAsync(e.GuildId, userId, InteractionKind.Coming, $"event {e.Id}");
        await UpdateVoiceAccessAsync(e, userId, status == RsvpStatuses.In);
        if (was == RsvpStatuses.In && status != RsvpStatuses.In)
            await MoveUpAsync(db, e);

        return status switch
        {
            RsvpStatuses.In => "You're in. 🎉",
            RsvpStatuses.Waiting => await WaitingTextAsync(db, e, userId),
            RsvpStatuses.Maybe => "Marked as maybe.",
            _ => "Marked as not coming.",
        };
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

    // Its planner's options (how it's settled, how many that takes, when the vote closes, what may be suggested, how a
    // change goes through, its voice channel): changed, and settled now when that makes it due. What it says.
    public async Task<string> ChangeRulesAsync(long eventId, Action<Event> change)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled } e)
                return "It's over or cancelled.";
            change(e);
            await db.SaveChangesAsync();
            var stage = EventStages.Of(e);
            if (stage == EventStage.Time && await SettleWhenDueAsync(db, e) is { } decided)
                return $"Saved. {decided}";
            if (stage == EventStage.Game && e.Decide == Deciding.AsSoonAsEnough && await EnoughForAsync(db, e) is { } ready)
                return $"Saved. {await SettleGameCoreAsync(db, e, ready)}";
            await RenderAsync(db, e);
            return "Saved.";
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
            if (e is null || e.State != EventStates.Scheduled || e.StartsAt is not null || !Suggesting.TimesAllowed(e.Suggestions))
                return "This vote doesn't take new times.";

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

    // A change for an event whose time is set (another time, another game for a session, or both), with the
    // suggester's vote; it may go through at once.
    public async Task<string> SuggestAsync(long eventId, ulong userId, DateTimeOffset? startsAt, EventSuggestion? game = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var now = time.GetUtcNow();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled, StartsAt: { } start } e || start <= now)
                return "It can't be changed any more.";
            if (!Suggesting.TimesAllowed(e.Suggestions) || game is not null && !Suggesting.GamesAllowed(e.Suggestions))
                return "Its planner doesn't take suggestions for that.";
            if (startsAt == start)
                startsAt = null;
            if (game?.GameId is { } gameId && (gameId == e.GameId || e.ChoosingGame))
                game = null;
            if (startsAt is null && game is null)
                return "That's how it is already: suggest another time or game.";
            if (startsAt <= now)
                return "That time has already passed.";
            var open = await db.EventSuggestions.Where(x => x.EventId == eventId).ToListAsync();
            if (open.Any(x => x.StartsAt == startsAt && x.GameId == game?.GameId))
                return "That's suggested already: press works for me on it.";
            if (open.Count >= MaxSuggestions)
                return $"It already has {MaxSuggestions} suggestions open.";

            var suggestion = new EventSuggestion
            {
                EventId = eventId,
                StartsAt = startsAt,
                GameId = game?.GameId,
                GameName = game?.GameName,
                Title = game?.Title,
                ById = userId,
                CreatedAt = now,
            };
            db.EventSuggestions.Add(suggestion);
            await db.SaveChangesAsync();
            db.EventSuggestionVotes.Add(new() { SuggestionId = suggestion.Id, UserId = userId });
            await db.SaveChangesAsync();
            if (await ChangedWhenAgreedAsync(db, e, suggestion) is { } changed)
                return changed;
            await RenderAsync(db, e);
            return $"Suggested {Describe(suggestion)}. {(await ChangingAsync(db, e)).Rule}";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Toggles whether a suggested change works for someone; it may go through.
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
            if (vote is null && await ChangedWhenAgreedAsync(db, e, suggestion) is { } changed)
                return changed;
            await RenderAsync(db, e);
            return vote is null ? $"Noted: {Describe(suggestion)} works for you." : "Taken back.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Whoever planned it, or someone with events.manage (checked by the caller): another time.
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
            return startsAt == start ? "That's its time already." : await ChangeCoreAsync(db, e, startsAt, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Whoever planned it, or someone with events.manage (checked by the caller): a suggested change, now.
    public async Task<string> ChangeToAsync(long suggestionId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.EventSuggestions.FindAsync(suggestionId) is not { } suggestion
                || await db.Events.FindAsync(suggestion.EventId) is not { State: EventStates.Scheduled, StartsAt: not null } e)
                return "That suggestion is gone.";
            return suggestion.StartsAt <= time.GetUtcNow() ? "That time has already passed." : await ChangeCoreAsync(db, e, suggestion.StartsAt, suggestion);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EventGameOption>> GameOptionsAsync(long eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.EventGameOptions.AsNoTracking().Where(o => o.EventId == eventId).OrderBy(o => o.Order).ToListAsync();
    }

    public async Task<EventSuggestion?> SuggestionAsync(long suggestionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.EventSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == suggestionId);
    }

    // The changes suggested for an event, oldest first, with who each works for.
    public async Task<IReadOnlyList<(EventSuggestion Suggestion, IReadOnlyList<ulong> WorksFor)>> SuggestionsAsync(long eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var open = await db.EventSuggestions.AsNoTracking().Where(x => x.EventId == eventId).OrderBy(x => x.Id).ToListAsync();
        var votes = await db.EventSuggestionVotes.AsNoTracking().Where(v => open.Select(x => x.Id).Contains(v.SuggestionId)).ToListAsync();
        return [.. open.Select(x => (x, (IReadOnlyList<ulong>)[.. votes.Where(v => v.SuggestionId == x.Id).Select(v => v.UserId)]))];
    }

    // A suggested change, as said: its time and/or game.
    public static string Describe(EventSuggestion x)
        => string.Join(" · ", new[] { x.StartsAt is { } at ? $"<t:{at.ToUnixTimeSeconds()}:F>" : null, x.GameName }.OfType<string>());

    // How a suggested change goes through, as said on its card; and how many of those who are in it takes.
    private async Task<(string Rule, int Needed)> ChangingAsync(BotDbContext db, Event e)
    {
        var going = (await AttendeesAsync(db, e.Id)).Count;
        return (e.Move ?? (await settings.GetAsync<EventRules>(e.GuildId, ModuleId)).Move) == Moving.Majority
            ? ($"It goes through when {Moving.Needed(going)} of the {going} who are in say it works.", Moving.Needed(going))
            : ($"<@{e.CreatorId}> makes the change.", 0);
    }

    private async Task<string?> ChangedWhenAgreedAsync(BotDbContext db, Event e, EventSuggestion suggestion)
    {
        if ((e.Move ?? (await settings.GetAsync<EventRules>(e.GuildId, ModuleId)).Move) != Moving.Majority)
            return null;
        var going = await AttendeesAsync(db, e.Id);
        var agreeing = await db.EventSuggestionVotes.Where(v => v.SuggestionId == suggestion.Id).Select(v => v.UserId).ToListAsync();
        return Moving.Agreed(going.Count, agreeing.Count(going.Contains)) ? await ChangeCoreAsync(db, e, suggestion.StartsAt, suggestion) : null;
    }

    // Another time and/or game. A new time: reminders and the start ping happen again, and an empty voice channel goes
    // (it opens again before the new start). A new game: the session's game, and its title when it followed the game.
    // A Discord event follows, and everyone who answered is told.
    private async Task<string> ChangeCoreAsync(BotDbContext db, Event e, DateTimeOffset? startsAt, EventSuggestion? game)
    {
        var was = e.StartsAt!.Value;
        var said = new List<string>();
        if (startsAt is { } at && at != was)
        {
            e.StartsAt = at;
            (e.ReminderSent, e.StartSent) = (!await RemindsAsync(e, time.GetUtcNow()), false);
            said.Add($"moved to <t:{at.ToUnixTimeSeconds()}:F> (<t:{at.ToUnixTimeSeconds()}:R>), from <t:{was.ToUnixTimeSeconds()}:F>");
        }
        if (game is { GameId: { } gameId, GameName: { } name } && gameId != e.GameId)
        {
            e.GameId = gameId;
            if (game.Title is { } title)
                e.Title = title;
            said.Add($"it's {name} now");
        }
        await db.EventSuggestions.Where(x => x.EventId == e.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        if (e.StartsAt != was && e.VoiceChannelId is { } voice && !presence.Snapshot(e.GuildId).Values.Any(p => p.ChannelId == voice))
            await CloseVoiceAsync(db, e);
        if (e.DiscordEventId is { } discordId)
        {
            var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
            var start = e.StartsAt!.Value;
            try
            {
                await rest.ModifyGuildScheduledEventAsync(e.GuildId, discordId, o => (o.Name, o.ScheduledStartTime, o.ScheduledEndTime) = (e.Title, start, start + rules.LengthOf(e)));
            }
            catch (RestException ex)
            {
                logger.LogWarning("Changing the Discord event of event {EventId} in {GuildId} failed: {Message}", e.Id, e.GuildId, ex.Message);
            }
        }
        await RenderAsync(db, e);

        var answered = await db.EventRsvps.Where(r => r.EventId == e.Id && r.Status != RsvpStatuses.Out).Select(r => r.UserId).ToListAsync();
        var change = string.Join(", and ", said);
        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, answered, $"**{e.Title}**: {change}", Link(e));
        await PostAsync(e, $"**{e.Title}**: {change}. {string.Join(' ', answered.Select(id => $"<@{id}>"))}", answered);
        return $"Changed: {change}.";
    }

    // While a session's game is voted on (its time set): another game to vote on, with the suggester's vote, which
    // signs them up. Titles follow the games when they did (the options carry them; null keeps the session's own).
    public async Task<string> AddGameAsync(long eventId, ulong userId, EventGameOption game)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled } e || EventStages.Of(e) != EventStage.Game)
                return "Games are suggested while the game is voted on.";
            if (!Suggesting.GamesAllowed(e.Suggestions))
                return "Its planner doesn't take suggestions for games.";
            var options = await db.EventGameOptions.Where(o => o.EventId == eventId).OrderBy(o => o.Order).ToListAsync();
            if (options.Any(o => o.GameId == game.GameId))
                return $"{game.Name} is one of its games already: vote for it.";
            if (options.Count >= MaxGames)
                return $"It already has {MaxGames} games.";
            var follows = options.All(o => o.Title is not null);
            var added = Option(e.Id, game, options.Count, follows ? game.Title : null);
            db.EventGameOptions.Add(added);
            options.Add(added);
            if (follows)
                e.Title = $"{string.Join(" or ", options.Select(o => o.Name))} session";
            await db.SaveChangesAsync();
            var said = await VoteGameCoreAsync(db, e, game.GameId, userId);
            return said.Settled ? said.Text : $"Added {game.Name}, with your vote.{said.Joined}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private static EventGameOption Option(long eventId, EventGameOption game, int order, string? title = null) => new()
    {
        EventId = eventId,
        GameId = game.GameId,
        Order = order,
        Name = game.Name,
        Emoji = game.Emoji,
        RoleId = game.RoleId,
        Players = game.Players,
        Title = title ?? game.Title,
    };

    // While the time or the game is voted on: can't make it (out; their time or game votes go), pressed again to take
    // it back.
    public async Task<string> CantMakeAsync(long eventId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.Events.FindAsync(eventId) is not { State: EventStates.Scheduled } e || EventStages.Of(e) == EventStage.Set)
                return "Voting on this has ended.";
            var was = await db.EventRsvps.FindAsync(eventId, userId);
            string said;
            if (was is { Status: RsvpStatuses.Out })
            {
                db.EventRsvps.Remove(was);
                await db.SaveChangesAsync();
                said = "Taken back.";
            }
            else if (e.StartsAt is null)
            {
                await SetRsvpAsync(db, eventId, userId, RsvpStatuses.Out);
                var options = db.EventTimeOptions.Where(o => o.EventId == eventId).Select(o => o.Id);
                await db.EventTimeVotes.Where(v => v.UserId == userId && options.Contains(v.OptionId)).ExecuteDeleteAsync();
                await db.SaveChangesAsync();
                said = "Noted: you can't make any of them.";
            }
            else
            {
                await RsvpCoreAsync(db, e, userId, RsvpStatuses.Out);
                said = "Noted: you can't make it.";
            }
            await RenderAsync(db, e);
            return said;
        }
        finally
        {
            _gate.Release();
        }
    }

    // While the game is voted on: each game with who'd play it (those fine with any count for each), who's in and
    // hasn't picked, who can't; a button each, Any is fine, Can't make it, and its planner's.
    private async Task<List<ActionRowProperties>> GameStageAsync(BotDbContext db, Event e, StringBuilder text, bool open)
    {
        var games = await db.EventGameOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.Order).ToListAsync();
        var picks = await db.EventGameVotes.Where(v => v.EventId == e.Id).ToListAsync();
        var rsvps = await db.EventRsvps.Where(r => r.EventId == e.Id).OrderBy(r => r.At).ToListAsync();
        var any = picks.Where(p => p.GameId == EventGameVote.AnyGame).Select(p => p.UserId).ToHashSet();
        string Mentions(IEnumerable<ulong> ids) => string.Join(", ", ids.Select(id => $"<@{id}>"));

        var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);
        text.AppendLine($"**Which game?** Press every game you'd play: that signs you up. Leave out any you won't. {Deciding.ExplainGame(e.Decide, await EnoughAsync(e), GameDue(e, rules), $"<@{e.CreatorId}>")}");
        if (e.Description is { } description)
            text.AppendLine().AppendLine(description);
        text.AppendLine();
        foreach (var game in games)
        {
            var who = picks.Where(p => p.GameId == game.GameId).Select(p => p.UserId).ToList();
            var count = who.Count + any.Count;
            text.AppendLine($"{game.Emoji ?? "🎮"} **{game.Name}** · {count}{(who.Count > 0 ? $": {Mentions(who)}" : "")}");
        }
        if (any.Count > 0)
            text.AppendLine($"👌 **Any is fine** (counted for each): {Mentions(any)}");
        var going = rsvps.Where(r => r.Status == RsvpStatuses.In).Select(r => r.UserId).ToList();
        text.AppendLine($"✅ **In** ({going.Count}{(e.Capacity is { } capacity ? $"/{capacity}" : "")}): {(going.Count == 0 ? "–" : Mentions(going))}");
        if (going.Where(id => picks.All(p => p.UserId != id)).ToList() is { Count: > 0 } unpicked)
            text.AppendLine($"-# Haven't picked yet (fine with any if they don't): {Mentions(unpicked)}");
        if (rsvps.Where(r => r.Status == RsvpStatuses.Waiting).Select(r => r.UserId).ToList() is { Count: > 0 } waiting)
            text.AppendLine($"⏳ **Waiting**: {Mentions(waiting)}");
        if (rsvps.Where(r => r.Status == RsvpStatuses.Out).Select(r => r.UserId).ToList() is { Count: > 0 } cant)
            text.AppendLine($"❌ **Can't**: {Mentions(cant)}");

        if (!open)
            return [];
        List<ActionRowProperties> rows =
        [
            new(games.Select(g => new ButtonProperties($"eventgame:{e.Id}:{g.GameId}", g.Name.Length <= 80 ? g.Name : g.Name[..80], ButtonStyle.Secondary)
            {
                Emoji = g.Emoji is { } emoji ? EmojiProperties.Standard(emoji) : null,
            })),
        ];
        var extras = new ActionRowProperties
        {
            new ButtonProperties($"eventgame:{e.Id}:{EventGameVote.AnyGame}", "Any is fine", EmojiProperties.Standard("👌"), ButtonStyle.Secondary),
            new ButtonProperties($"eventcant:{e.Id}", "Can't make it", ButtonStyle.Secondary),
        };
        if (await SuggestsGamesAsync(db, e))
            extras.Add(new ButtonProperties($"eventsuggestgame:{e.Id}", "Suggest a game", EmojiProperties.Standard("🎮"), ButtonStyle.Secondary));
        extras.Add(new ButtonProperties($"eventsettle:{e.Id}", "Settle now", ButtonStyle.Secondary));
        rows.Add(extras);
        return rows;
    }

    // Before the start: the changes suggested (and how they go through) on the card, a row each to agree or make the
    // change, and one to suggest another, when its planner takes suggestions.
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
                text.AppendLine($"💡 **{i + 1}.** {Describe(open[i])}, suggested by <@{open[i].ById}> · works for {(works.Count == 0 ? "nobody yet" : string.Join(", ", works))}");
            }
            text.AppendLine($"-# {(await ChangingAsync(db, e)).Rule}");
        }
        List<ActionRowProperties> rows = [.. open.Select((x, i) => new ActionRowProperties
        {
            new ButtonProperties($"eventworks:{x.Id}", $"{i + 1} works for me", EmojiProperties.Standard("👍"), ButtonStyle.Secondary),
            new ButtonProperties($"eventmoveto:{x.Id}", $"Change to {i + 1}", ButtonStyle.Secondary),
        })];
        if (open.Count < MaxSuggestions && Suggesting.TimesAllowed(e.Suggestions))
            rows.Insert(0, new ActionRowProperties { new ButtonProperties($"eventsuggest:{e.Id}", "Suggest a change", EmojiProperties.Standard("💡"), ButtonStyle.Secondary) });
        return rows;
    }

    // Whether a session whose game is voted on takes another: its planner allows it and it has room.
    private static async Task<bool> SuggestsGamesAsync(BotDbContext db, Event e)
        => EventStages.Of(e) == EventStage.Game && Suggesting.GamesAllowed(e.Suggestions)
            && await db.EventGameOptions.CountAsync(o => o.EventId == e.Id) < MaxGames;

    // A button in the first row after the answers with room for it, else a row of its own.
    private static void Place(List<ActionRowProperties> rows, ButtonProperties button)
    {
        if (rows.Skip(1).FirstOrDefault(r => r.Count() < 5) is { } room)
            room.Add(button);
        else if (rows.Count < 5)
            rows.Add([button]);
    }

    // Whether a reminder is still to come: not when the start is already within the reminder's time, as for one
    // planned (or moved) shortly before, whose own message just pinged everyone.
    private async Task<bool> RemindsAsync(Event e, DateTimeOffset now)
        => e.StartsAt is { } start && (await settings.GetAsync<EventRules>(e.GuildId, ModuleId)).ReminderMinutes is var minutes and > 0
            && start - now > TimeSpan.FromMinutes(minutes);

    // A game someone would play, or Any (AnyGame), pressed again to take it back; one takes the place of the other.
    // It signs them up; enough for one game settles it, as its rule says.
    public async Task<string> VoteGameAsync(long eventId, long gameId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is not { State: EventStates.Scheduled } || EventStages.Of(e) != EventStage.Game)
                return e is { State: EventStates.Scheduled, StartsAt: null } ? "First the time: the game is picked once it's set." : "The game is settled already.";
            if (gameId != EventGameVote.AnyGame && await db.EventGameOptions.FindAsync(eventId, gameId) is null)
                return "That isn't one of its games.";
            return (await VoteGameCoreAsync(db, e, gameId, userId)).Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate, while the game is voted on. Joined: what signing up said, when it did.
    private async Task<(string Text, bool Settled, string Joined)> VoteGameCoreAsync(BotDbContext db, Event e, long gameId, ulong userId)
    {
        var mine = await db.EventGameVotes.Where(v => v.EventId == e.Id && v.UserId == userId).ToListAsync();
        var again = mine.FirstOrDefault(v => v.GameId == gameId);
        if (again is not null)
            db.EventGameVotes.Remove(again);
        else
        {
            db.EventGameVotes.RemoveRange(mine.Where(v => gameId == EventGameVote.AnyGame || v.GameId == EventGameVote.AnyGame));
            db.EventGameVotes.Add(new() { EventId = e.Id, UserId = userId, GameId = gameId });
        }
        await db.SaveChangesAsync();
        var joined = "";
        if (again is null && (await db.EventRsvps.FindAsync(e.Id, userId))?.Status is not (RsvpStatuses.In or RsvpStatuses.Waiting))
            joined = $" {await RsvpCoreAsync(db, e, userId, RsvpStatuses.In)}";
        if (again is null && e.Decide == Deciding.AsSoonAsEnough && await EnoughForAsync(db, e) is { } ready)
            return (await SettleGameCoreAsync(db, e, ready), true, joined);
        await RenderAsync(db, e);
        var name = gameId == EventGameVote.AnyGame ? "any of them" : (await db.EventGameOptions.FindAsync(e.Id, gameId))?.Name ?? "it";
        return (again is null ? $"You'd play {name}. 👍{joined}" : "Taken back.", false, joined);
    }

    // Who'd play each game, in order: its own votes and those fine with any.
    private static async Task<(List<EventGameOption> Options, int[] Counts)> GameCountsAsync(BotDbContext db, Event e)
    {
        var options = await db.EventGameOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.Order).ToListAsync();
        var votes = await db.EventGameVotes.Where(v => v.EventId == e.Id).ToListAsync();
        var any = votes.Count(v => v.GameId == EventGameVote.AnyGame);
        return (options, [.. options.Select(o => votes.Count(v => v.GameId == o.GameId) + any)]);
    }

    // The game enough would play (the most, the first of a tie), if one has.
    private async Task<long?> EnoughForAsync(BotDbContext db, Event e)
    {
        var (options, counts) = await GameCountsAsync(db, e);
        var enough = await EnoughAsync(e);
        return Deciding.Favourite([.. counts.Select(c => c >= enough ? c : 0)]) is { } i && counts[i] >= enough ? options[i].GameId : null;
    }

    // The game most would play (the first of a tie) becomes the session's: its title (unless it has its own) and its
    // player count (unless it was given one).
    private static async Task<EventGameOption?> ChooseGameAsync(BotDbContext db, Event e, long? gameId = null)
    {
        var (options, counts) = await GameCountsAsync(db, e);
        var chosen = gameId is { } id ? options.FirstOrDefault(o => o.GameId == id)
            : Deciding.Favourite(counts) is { } i ? options[i] : null;
        if (chosen is null)
            return null;
        (e.GameId, e.ChoosingGame, e.Capacity) = (chosen.GameId, false, e.Capacity ?? chosen.Players);
        if (chosen.Title is { } title)
            e.Title = title;
        await db.SaveChangesAsync();
        return chosen;
    }

    // A session whose time is set and whose game is voted on: settled now, by the given game or the most votes. Its
    // Discord event takes the new title, and those who are in hear which game it is.
    public async Task<string> SettleGameAsync(long eventId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Events.FindAsync(eventId) is { State: EventStates.Scheduled, StartsAt: not null, ChoosingGame: true } e
                ? await SettleGameCoreAsync(db, e, null)
                : "The game is settled already.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Those in who picked games but not this one are named, to say if they're out.
    private async Task<string> SettleGameCoreAsync(BotDbContext db, Event e, long? gameId)
    {
        var picks = await db.EventGameVotes.Where(v => v.EventId == e.Id).ToListAsync();
        if (await ChooseGameAsync(db, e, gameId) is not { } chosen)
            return "It has no games to choose from.";
        await db.EventGameVotes.Where(v => v.EventId == e.Id).ExecuteDeleteAsync();
        if (e.DiscordEventId is { } discordId)
        {
            try
            {
                await rest.ModifyGuildScheduledEventAsync(e.GuildId, discordId, o => o.Name = e.Title);
            }
            catch (RestException ex)
            {
                logger.LogWarning("Renaming the Discord event of event {EventId} in {GuildId} failed: {Message}", e.Id, e.GuildId, ex.Message);
            }
        }
        await RenderAsync(db, e);
        var going = await AttendeesAsync(db, e.Id);
        var other = going.Where(id => picks.Any(p => p.UserId == id) && picks.Where(p => p.UserId == id).All(p => p.GameId != chosen.GameId && p.GameId != EventGameVote.AnyGame)).ToList();
        await PostAsync(e, $"**{e.Title}**: it's {chosen.Name}! {string.Join(' ', going.Select(id => $"<@{id}>"))}"
            + (other.Count == 0 ? "" : $"\n{string.Join(", ", other.Select(id => $"<@{id}>"))}: you didn't pick it, so press Out on the card if you're out."), going);
        return $"It's {chosen.Name}.";
    }

    // When a set time's game vote is settled without enough votes for one: when the reminder goes out (else at the
    // start), and at the start when its planner settles it. One that began late (its time settled close to the start)
    // still gets GameAtLeast, up to the start.
    public static DateTimeOffset GameDue(Event e, EventRules rules)
    {
        var start = e.StartsAt!.Value;
        var due = start - (e.Decide == Deciding.PlannerDecides || rules.ReminderMinutes == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(rules.ReminderMinutes));
        return e.PollClosesAt is { } began && began + GameAtLeast > due ? (began + GameAtLeast < start ? began + GameAtLeast : start) : due;
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

    // Games: each session of the series asks which of these to play.
    public async Task<EventSeries> CreateSeriesAsync(EventSeries series, IReadOnlyList<EventSeriesGame>? games = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.EventSeries.Add(series);
            await db.SaveChangesAsync();
            if (games is { Count: > 1 })
            {
                db.EventSeriesGames.AddRange(games.Select((g, i) => new EventSeriesGame
                {
                    SeriesId = series.Id,
                    GameId = g.GameId,
                    Order = i,
                    Name = g.Name,
                    Emoji = g.Emoji,
                    RoleId = g.RoleId,
                    Players = g.Players,
                    Title = g.Title,
                }));
                await db.SaveChangesAsync();
            }
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

                var games = await db.EventSeriesGames.AsNoTracking().Where(g => g.SeriesId == series.Id).OrderBy(g => g.Order).ToListAsync(ct);
                await AddAsync(new()
                {
                    ChoosingGame = games.Count > 1,
                    Decide = series.Decide,
                    Enough = series.Enough,
                    Move = series.Move,
                    Suggestions = series.Suggestions,
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
                }, [], [.. games.Select(g => new EventGameOption { GameId = g.GameId, Name = g.Name, Emoji = g.Emoji, RoleId = g.RoleId, Players = g.Players, Title = g.Title })]);
            }
        }
    }

    private async Task<Event> AddAsync(Event e, IReadOnlyList<DateTimeOffset> times, IReadOnlyList<EventGameOption>? games = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            // The creator of a one-off is going, presumably; a series doesn't sign them up every time.
            var going = e.StartsAt is not null && e.SeriesId is null ? new[] { e.CreatorId } : [];
            e.ReminderSent = !await RemindsAsync(e, e.CreatedAt);
            // A vote on the game pings every one of the games' roles (games may share one; Discord refuses it twice).
            ulong[] roles = e.PingRoleId is { } role ? [role] : [.. (games ?? []).Select(g => g.RoleId).OfType<ulong>().Distinct()];
            await AddCoreAsync(db, e, times, going, roles.Length == 0 ? null : string.Join(' ', roles.Select(r => $"<@&{r}>")), here: e.ChannelId != await forum.ForumOfAsync(e.GuildId),
                games: games, roles: roles);

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
    private async Task<Event> AddCoreAsync(BotDbContext db, Event e, IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ulong> going, string? content, bool here = true,
        IReadOnlyList<EventGameOption>? games = null, IReadOnlyList<ulong>? roles = null)
    {
        db.Events.Add(e);
        await db.SaveChangesAsync();
        if (e.ChoosingGame)
        {
            db.EventGameOptions.AddRange((games ?? []).Select((g, i) => new EventGameOption
            {
                EventId = e.Id,
                GameId = g.GameId,
                Order = i,
                Name = g.Name,
                Emoji = g.Emoji,
                RoleId = g.RoleId,
                Players = g.Players,
                Title = g.Title,
            }));
            await db.SaveChangesAsync();
        }
        roles ??= e.PingRoleId is { } pinged ? [pinged] : [];

        foreach (var userId in going)
            db.EventRsvps.Add(new() { EventId = e.Id, UserId = userId, Status = RsvpStatuses.In, At = e.CreatedAt });
        foreach (var at in times.Distinct().Order())
            db.EventTimeOptions.Add(new() { EventId = e.Id, StartsAt = at, ProposedById = e.CreatorId, CreatedAt = e.CreatedAt });
        await db.SaveChangesAsync();

        var (embed, components) = await BuildAsync(db, e);
        if (here)
        {
            RestMessage message;
            try
            {
                message = await rest.SendMessageAsync(e.ChannelId, new()
                {
                    Content = content,
                    Embeds = [embed],
                    Components = components,
                    AllowedMentions = new() { AllowedRoles = [.. roles], AllowedUsers = going.ToList() },
                });
            }
            catch (RestException)
            {
                // Without its card nobody could answer it, yet it would remind and ping: it isn't kept.
                db.Events.Remove(e);
                await db.SaveChangesAsync();
                throw;
            }
            e.MessageId = message.Id;
            await db.SaveChangesAsync();
        }
        var mentions = new AllowedMentionsProperties { AllowedRoles = [.. roles], AllowedUsers = going.ToList() };
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

        (e.StartsAt, e.PollClosesAt) = (winner.StartsAt, now);
        e.ReminderSent = !await RemindsAsync(e, now);
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
            + (waiting.Count == 0 ? "" : $" It's full; waiting: {string.Join(' ', waiting.Select(id => $"<@{id}>"))}.")
            + (e.ChoosingGame ? $"\nNow the game: press every one you'd play on the card{(Link(e) is { } link ? $" ({link})" : "")}." : ""), voted);
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

                if (e.ChoosingGame && now >= GameDue(e, rules))
                    await SettleGameCoreAsync(db, e, null);

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
            var cant = await db.EventRsvps.Where(r => r.EventId == e.Id && r.Status == RsvpStatuses.Out).Select(r => $"<@{r.UserId}>").ToListAsync();
            if (cant.Count > 0)
                text.AppendLine($"Can't make any: {string.Join(", ", cant)}");
            if (e.ChoosingGame)
            {
                var games = await db.EventGameOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.Order).Select(o => o.Name).ToListAsync();
                text.AppendLine().AppendLine($"-# Once the time is set, those coming pick the game: {string.Join(", ", games)}.");
            }
            var extras = new ActionRowProperties();
            if (open)
            {
                extras.Add(new ButtonProperties($"eventcant:{e.Id}", "Can't make any", ButtonStyle.Secondary));
                if (Suggesting.TimesAllowed(e.Suggestions) && options.Count < MaxTimeOptions)
                    extras.Add(new ButtonProperties($"eventpropose:{e.Id}", "Suggest a time", EmojiProperties.Standard("💡"), ButtonStyle.Secondary));
                extras.Add(new ButtonProperties($"eventsettle:{e.Id}", "Settle now", ButtonStyle.Secondary));
                extras.Add(OptionsButton(e));
                rows.Add(extras);
            }
            components = rows;
        }
        else if (open && EventStages.Of(e) == EventStage.Game)
        {
            var start = e.StartsAt!.Value.ToUnixTimeSeconds();
            text.AppendLine($"<t:{start}:F> (<t:{start}:R>)" + (e.LengthMinutes is { } minutes ? $" · {Durations.Format(TimeSpan.FromMinutes(minutes))}" : "")).AppendLine();
            var rows = await GameStageAsync(db, e, text, open);
            var last = new ActionRowProperties { OptionsButton(e) };
            if (await calendars.AddPageAsync(e.GuildId, e.Id) is { } add)
                last.Add(new LinkButtonProperties(add, "Add to calendar", EmojiProperties.Standard("📅")));
            rows.Add(last);
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
            List<ActionRowProperties> rows = [row];
            rows.AddRange(await SuggestionRowsAsync(db, e, text, open && e.StartsAt > time.GetUtcNow()));
            if (open)
                Place(rows, OptionsButton(e));
            components = rows;
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

    // Its planner's (or events.manage's): the rules and details left out of the command.
    private static ButtonProperties OptionsButton(Event e) => new($"eventoptions:{e.Id}", "Options", EmojiProperties.Standard("⚙️"), ButtonStyle.Secondary);

    private static string Kind(Event e) => e.GameId is null && !e.ChoosingGame ? EventHappening.Id : SessionHappening.Id;

    private static string? Link(Event e) => e.MessageId is { } m ? Notifier.Link(e.GuildId, e.ChannelId, m) : null;
}
