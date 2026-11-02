using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Events;

[SlashCommand("event", "Plan events and see who's coming", Contexts = [InteractionContextType.Guild])]
public sealed class EventCommands(
    EventBoard board,
    TimeZones zones,
    ModuleState modules,
    SettingsStore settings,
    AccessControl access,
    CalendarLinks calendars,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : GuildCommandModule
{
    [SubSlashCommand("plan", "Plan an event: at a set time, or let people vote between several (and suggest their own)")]
    public async Task PlanAsync(
        [SlashCommandParameter(Description = "What's happening", MaxLength = 100)] string title,
        [SlashCommandParameter(Description = "When, in your time: now, fri 8pm, tomorrow 19:30, in 2h; several with commas make it a vote", MaxLength = 400)] string when,
        [SlashCommandParameter(Description = "What may be suggested: more times to vote on, then a change (default: the server's setting)")] Suggestable? suggestions = null,
        [SlashCommandParameter(Name = "closes-in-hours", Description = "A vote ends after this long (default 24)", MinValue = 1, MaxValue = 720)] int closesInHours = 24,
        [SlashCommandParameter(Name = "settled-by", Description = "How a vote on the time is settled (default: the server's setting)")] Settling? settledBy = null,
        [SlashCommandParameter(Name = "people-needed", Description = "With \"as soon as enough\": how many (default: the limit, else the server's number)", MinValue = 1, MaxValue = 500)] int? peopleNeeded = null,
        [SlashCommandParameter(Name = "moves-when", Description = "Once its time is set, when a suggested change goes through (default: the server's setting)")] Moves? movesWhen = null,
        [SlashCommandParameter(Description = "More details", MaxLength = 1000)] string? description = null,
        [SlashCommandParameter(Description = "A role to ping about it")] Role? ping = null,
        [SlashCommandParameter(Description = "A voice channel shortly before the start: open, or locked to those who are in")] EventVoice voice = EventVoice.None,
        [SlashCommandParameter(Name = "discord-event", Description = "Also list it in the server's Discord events (default: the server setting)")] bool? discordEvent = null,
        [SlashCommandParameter(Description = "Most people who can be in; more go on a waiting list", MinValue = 1, MaxValue = 500)] int? limit = null,
        [SlashCommandParameter(Description = "How long it lasts, e.g. 90m or 2h (default: the server's usual length)", MaxLength = 10)] string? length = null)
    {
        if (await RefusalAsync() is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        var (minutes, lengthProblem) = EventRules.ReadLength(length);
        if (lengthProblem is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(lengthProblem)));
            return;
        }

        var (zone, own) = await zones.ForAsync(GuildId, Context.User.Id);
        var now = Instant.FromDateTimeOffset(time.GetUtcNow());
        var parsed = when.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => (Text: t, When: WhenParser.Parse(t, zone, now)))
            .ToList();
        var problem = parsed.FirstOrDefault(p => p.When.At is null);
        var error = parsed.Count == 0 ? "Give at least one time."
            : parsed.Count > EventBoard.MaxTimeOptions ? $"At most {EventBoard.MaxTimeOptions} times."
            : problem.Text is not null ? $"`{problem.Text}`: {problem.When.Problem}"
            : null;
        if (error is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"{error}\n{TimeZones.Hint(zone, own)}")));
            return;
        }

        // Posting the event, pinging and DMing can take a moment.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var times = parsed.Select(p => p.When.At!.Value.ToDateTimeOffset()).ToList();
        var vote = times.Count > 1;
        var suggesting = suggestions is { } chose ? Suggesting.Of(chose) : (await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId)).Suggestions;
        if (vote)
        {
            var closesAt = time.GetUtcNow() + TimeSpan.FromHours(closesInHours);
            var poll = await board.CreatePollAsync(GuildId, Context.Channel.Id, Context.User.Id, title.Trim(), description?.Trim(), ping?.Id,
                times, closesAt, suggesting, VoiceMode(voice), await WantsDiscordEventAsync(discordEvent), capacity: limit, lengthMinutes: minutes,
                decide: settledBy is { } chosen ? Deciding.Of(chosen) : (await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId)).Decide, enough: peopleNeeded,
                move: movesWhen is { } moves ? Moving.Of(moves) : null);
            await ModifyResponseAsync(m => m.Content = $"Poll {poll.Id} is up. Times were read in {zone.Id}. {TimeZones.Hint(zone, own)}");
            return;
        }
        var e = await board.CreateAsync(GuildId, Context.Channel.Id, Context.User.Id, title.Trim(), description?.Trim(), ping?.Id, times[0], VoiceMode(voice), await WantsDiscordEventAsync(discordEvent), capacity: limit, lengthMinutes: minutes,
            move: movesWhen is { } moving ? Moving.Of(moving) : null, suggestions: suggesting);
        var unix = times[0].ToUnixTimeSeconds();
        await ModifyResponseAsync(m => m.Content = $"Event {e.Id} planned for <t:{unix}:F> (<t:{unix}:R>), read in {zone.Id}. {TimeZones.Hint(zone, own)}");
    }

    [SubSlashCommand("move", "Move an event to another time (yours, or any with events.manage); those who answered are told")]
    public async Task MoveAsync(
        [SlashCommandParameter(Description = "Event", AutocompleteProviderType = typeof(EventAutocomplete))] long @event,
        [SlashCommandParameter(Description = "When, in your time: sat 21:00, tomorrow 19:30", MaxLength = 50)] string when)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await EventMoving.MoveAsync(board, access, zones, time, Guild, (GuildUser)Context.User, @event, when);
        await ModifyResponseAsync(m => m.Content = result);
    }

    [SubSlashCommand("decide", "Settle a poll now: a given option, or the one with most votes")]
    public async Task DecideAsync(
        [SlashCommandParameter(Description = "Event", AutocompleteProviderType = typeof(EventAutocomplete))] long @event,
        [SlashCommandParameter(Description = "Option number from the poll (leave out for the most votes)", MinValue = 1, MaxValue = EventBoard.MaxTimeOptions)] int? option = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == @event && x.GuildId == GuildId);
        var refusal = e is null || e.State != EventStates.Scheduled || e.StartsAt is not null ? "There's no such open poll."
            : e.CreatorId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents)
                ? $"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can decide it."
                : null;
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }

        long? optionId = null;
        if (option is { } n)
        {
            var options = await db.EventTimeOptions.Where(o => o.EventId == e!.Id).OrderBy(o => o.StartsAt).Select(o => o.Id).ToListAsync();
            if (n > options.Count)
            {
                await RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"The poll has {options.Count} options.")));
                return;
            }
            optionId = options[n - 1];
        }

        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.DecideAsync(e!.Id, optionId);
        await ModifyResponseAsync(m => m.Content = result);
    }

    [SubSlashCommand("recurring", "A regular slot, e.g. every Friday 20:00; each one opens as an event ahead of time")]
    public async Task RecurringAsync(
        [SlashCommandParameter(Description = "What's happening", MaxLength = 100)] string title,
        [SlashCommandParameter(Description = "fri, or mon, thu, or daily, weekdays, weekends", MaxLength = 60)] string days,
        [SlashCommandParameter(Name = "time", Description = "In your time, e.g. 20:00 or 8pm", MaxLength = 10)] string clockText,
        [SlashCommandParameter(Name = "open-days-ahead", Description = "How early each event opens (default 3 days)", MinValue = 1, MaxValue = 30)] int openDaysAhead = 3,
        [SlashCommandParameter(Description = "More details", MaxLength = 1000)] string? description = null,
        [SlashCommandParameter(Description = "A role to ping about each one")] Role? ping = null,
        [SlashCommandParameter(Description = "A voice channel shortly before each start: open, or locked to those who are in")] EventVoice voice = EventVoice.None,
        [SlashCommandParameter(Name = "discord-event", Description = "Also list each in the server's Discord events (default: the server setting)")] bool? discordEvent = null,
        [SlashCommandParameter(Description = "Most people who can be in each one; more go on a waiting list", MinValue = 1, MaxValue = 500)] int? limit = null,
        [SlashCommandParameter(Description = "How long each lasts, e.g. 90m or 2h (default: the server's usual length)", MaxLength = 10)] string? length = null,
        [SlashCommandParameter(Name = "moves-when", Description = "When a suggested change goes through (default: the server's setting)")] Moves? movesWhen = null,
        [SlashCommandParameter(Description = "What people may suggest once one is open: another time (default: the server's setting)")] Suggestable? suggestions = null)
    {
        if (await RefusalAsync() is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }
        var (minutes, lengthProblem) = EventRules.ReadLength(length);
        if (lengthProblem is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(lengthProblem)));
            return;
        }

        var (zone, own) = await zones.ForAsync(GuildId, Context.User.Id);
        if (Recurrence.ParseDays(days) is not { } dayList)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Days are like `fri`, `mon, thu`, `daily`, `weekdays` or `weekends`.")));
            return;
        }
        if (WhenParser.ParseClock(clockText) is not { } clock)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("The time is like `20:00` or `8pm`.")));
            return;
        }

        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var series = await board.CreateSeriesAsync(new()
        {
            GuildId = GuildId,
            ChannelId = Context.Channel.Id,
            CreatorId = Context.User.Id,
            Title = title.Trim(),
            Description = description?.Trim(),
            PingRoleId = ping?.Id,
            Days = dayList,
            TimeOfDay = clock.Hour * 60 + clock.Minute,
            Zone = zone.Id,
            OpenDaysAhead = openDaysAhead,
            VoiceMode = VoiceMode(voice),
            WantsDiscordEvent = await WantsDiscordEventAsync(discordEvent),
            Capacity = limit,
            LengthMinutes = minutes,
            Move = movesWhen is { } moves ? Moving.Of(moves) : null,
            Suggestions = suggestions is { } suggesting ? Suggesting.Of(suggesting) : (await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId)).Suggestions,
            CreatedAt = time.GetUtcNow(),
        });
        await ModifyResponseAsync(m => m.Content =
            $"Series {series.Id}: **{series.Title}**, {Recurrence.Describe(dayList)} at {clock:HH:mm} {zone.Id} time. Each one opens {openDaysAhead} days ahead. {TimeZones.Hint(zone, own)}");
    }

    [SubSlashCommand("recurring-stop", "Stop a recurring event (events already opened stay)")]
    public async Task<InteractionMessageProperties> RecurringStopAsync(
        [SlashCommandParameter(Description = "Series", AutocompleteProviderType = typeof(SeriesAutocomplete))] long series)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var found = await db.EventSeries.AsNoTracking().FirstOrDefaultAsync(s => s.Id == series && s.GuildId == GuildId && s.Active);
        if (found is null)
            return Replies.Ephemeral("There's no such recurring event.");
        if (found.CreatorId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents))
            return Replies.Ephemeral($"Only <@{found.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can stop it.");

        await board.StopSeriesAsync(found.Id);
        return Replies.Ephemeral($"Stopped **{found.Title}**. Events already opened stay; cancel them with `/event cancel` if needed.");
    }

    [SubSlashCommand("limit", "Change how many can be in; more room moves those waiting up")]
    public async Task<InteractionMessageProperties> LimitAsync(
        [SlashCommandParameter(Description = "Event", AutocompleteProviderType = typeof(EventAutocomplete))] long @event,
        [SlashCommandParameter(Description = "Most people who can be in; 0 for no limit", MinValue = 0, MaxValue = 500)] int limit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == @event && x.GuildId == GuildId);
        if (e is null || e.State != EventStates.Scheduled)
            return Replies.Ephemeral("There's no such upcoming event.");
        if (e.CreatorId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents))
            return Replies.Ephemeral($"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can change it.");

        var moved = await board.ChangeLimitAsync(e.Id, x => x.Capacity = limit == 0 ? null : limit);
        return Replies.Ephemeral(LimitText(e.Title, limit == 0 ? null : limit, moved));
    }

    public static string LimitText(string title, int? limit, int? moved)
        => moved is null ? "That event isn't upcoming anymore."
            : $"**{title}** {(limit is { } l ? $"takes {l} now" : "has no limit now")}.{(moved > 0 ? $" {moved} moved up from the waiting list." : "")}";

    [SubSlashCommand("length", "Change how long an event lasts (for calendars and Discord's event list)")]
    public async Task<InteractionMessageProperties> LengthAsync(
        [SlashCommandParameter(Description = "Event", AutocompleteProviderType = typeof(EventAutocomplete))] long @event,
        [SlashCommandParameter(Description = "e.g. 90m or 2h; none for the server's usual length", MaxLength = 10)] string length)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == @event && x.GuildId == GuildId);
        if (e is null || e.State != EventStates.Scheduled)
            return Replies.Ephemeral("There's no such upcoming event.");
        if (e.CreatorId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents))
            return Replies.Ephemeral($"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can change it.");
        var usual = length.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
        var (minutes, problem) = usual ? (null, null) : EventRules.ReadLength(length);
        if (problem is not null || !usual && minutes is null)
            return Replies.Ephemeral(problem ?? "Give a length, like 90m or 2h.");
        return Replies.Ephemeral(await board.ChangeLengthAsync(e.Id, minutes)
            ? $"**{e.Title}** {(minutes is { } m ? $"lasts {Durations.Format(TimeSpan.FromMinutes(m))}" : "lasts the server's usual length")} now."
            : "That event isn't upcoming anymore.");
    }

    [SubSlashCommand("calendar", "This server's events in your calendar app (your own choice of them: /me calendar)")]
    public async Task<InteractionMessageProperties> CalendarAsync(
        [SlashCommandParameter(Name = "new-link", Description = "Make a new link; the old one stops working (needs " + BotPermissions.ManageEvents + ")")] bool newLink = false)
    {
        if (newLink && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents))
            return Replies.Ephemeral($"A new link needs `{BotPermissions.ManageEvents}`.");
        var link = await calendars.ServerLinkAsync(GuildId, renew: newLink);
        return Replies.Ephemeral(await calendars.AddressOfAsync(link) is var (https, webcal)
            ? $"{(newLink ? "New link; the old one stopped working.\n" : "")}Subscribe to this in your calendar app, and the server's events stay in it, kept up to date:\n{https}\n-# Google Calendar: Other calendars → From URL. Apple: {webcal}"
            : "The panel's address isn't known yet, so there's no link to give.");
    }

    [SubSlashCommand("cancel", "Call an event off")]
    public async Task CancelAsync(
        [SlashCommandParameter(Description = "Event", AutocompleteProviderType = typeof(EventAutocomplete))] long @event)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == @event && x.GuildId == GuildId);
        var refusal = e is null || e.State != EventStates.Scheduled ? "There's no such upcoming event."
            : e.CreatorId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents)
                ? $"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can cancel it."
                : null;
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        // Its card, forum post, voice channel and Discord event all go, which can take longer than an answer may.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        await board.CancelAsync(e!);
        await ModifyResponseAsync(m => m.Content = $"Cancelled **{e!.Title}**.");
    }

    [SubSlashCommand("list", "Upcoming events")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var upcoming = await db.Events
            .Where(e => e.GuildId == GuildId && e.State == EventStates.Scheduled)
            .OrderBy(e => e.StartsAt ?? e.PollClosesAt)
            .Take(15)
            .ToListAsync();

        var series = await db.EventSeries.Where(s => s.GuildId == GuildId && s.Active).ToListAsync();
        var recurring = series.Count == 0 ? "" : "\n\n**Recurring:**\n" + string.Join('\n', series.Select(s => $"{s.Title}: {Recurrence.Describe(s.Days)} at {LocalTime.FromMinutesSinceMidnight(s.TimeOfDay):HH:mm} {s.Zone} (series {s.Id})"));

        var events = upcoming.Count == 0
            ? "Nothing planned. Start something with `/event plan`."
            : string.Join('\n', upcoming.Select(Line));
        return Replies.Ephemeral(events + recurring);

        string Line(Event e)
        {
            var when = e.StartsAt is { } s ? $"<t:{s.ToUnixTimeSeconds()}:f>" : $"voting until <t:{e.PollClosesAt!.Value.ToUnixTimeSeconds()}:f>";
            var link = e.MessageId is { } m ? $" ([open](https://discord.com/channels/{GuildId}/{e.ChannelId}/{m}))" : "";
            return $"{when} **{e.Title}**{link}";
        }
    }

    private async Task<InteractionMessageProperties?> RefusalAsync()
    {
        if (!await modules.IsEnabledAsync(GuildId, EventBoard.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(EventBoard.ModuleId));
        var rules = await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId);
        if (rules.CreateNeedsPermission && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.CreateEvents))
            return Replies.Ephemeral($"Planning events needs `{BotPermissions.CreateEvents}` here.");
        return null;
    }

    private static string? VoiceMode(EventVoice voice) => voice switch
    {
        EventVoice.Open => VoiceModes.Open,
        EventVoice.Locked => VoiceModes.Locked,
        _ => null,
    };

    private async Task<bool> WantsDiscordEventAsync(bool? asked)
        => asked ?? (await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId)).DiscordEvents;
}

public sealed class EventButtons(EventBoard board) : GuildButtonModule
{
    [ComponentInteraction("event")]
    public async Task RsvpAsync(long eventId, string status)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.RsvpAsync(eventId, Context.User.Id, status);
        await ModifyResponseAsync(m => m.Content = result);
    }

    [ComponentInteraction("eventanother")]
    public async Task AnotherAsync(long eventId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.OpenAnotherAsync(eventId, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = result);
    }
}

public sealed class EventAutocomplete(IDbContextFactory<BotDbContext> dbFactory) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var guildId = context.Interaction.GuildId!.Value;
        await using var db = await dbFactory.CreateDbContextAsync();
        var events = await db.Events.Where(e => e.GuildId == guildId && e.State == EventStates.Scheduled).OrderBy(e => e.StartsAt ?? e.PollClosesAt).Take(100).ToListAsync();
        return events
            .Where(e => e.Title.Contains(input, StringComparison.OrdinalIgnoreCase) || e.Id.ToString() == input)
            .Take(25)
            .Select(e => (e.Id, Label: $"{e.Id}: {e.Title} ({(e.StartsAt is { } s ? $"{s:yyyy-MM-dd HH:mm} UTC" : "poll")})"))
            .Select(e => new ApplicationCommandOptionChoiceProperties(e.Label.Length <= 100 ? e.Label : e.Label[..100], e.Id));
    }
}

public sealed class EventPollButtons(EventBoard board, AccessControl access) : GuildButtonModule
{
    [ComponentInteraction("eventgame")]
    public async Task VoteGameAsync(long eventId, long gameId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.VoteGameAsync(eventId, gameId, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = result);
    }

    // Whoever planned it, or someone with events.manage: the poll is settled now, by the most votes.
    [ComponentInteraction("eventsettle")]
    public async Task SettleAsync(long eventId)
    {
        var e = await board.FindAsync(GuildId, eventId);
        var refusal = e is null || e.State != EventStates.Scheduled || e.StartsAt is not null && !e.ChoosingGame ? "This vote is already settled."
            : e.CreatorId != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageEvents)
                ? $"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can settle it."
                : null;
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = e!.StartsAt is null ? await board.DecideAsync(e.Id, null) : await board.SettleGameAsync(e.Id);
        await ModifyResponseAsync(m => m.Content = result);
    }

    [ComponentInteraction("eventvote")]
    public async Task VoteAsync(long optionId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.VoteAsync(optionId, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = result);
    }

    [ComponentInteraction("eventpropose")]
    public InteractionCallbackProperties Propose(long eventId)
        => InteractionCallback.Modal(new ModalProperties($"eventproposetime:{eventId}", "Propose a time")
        {
            new LabelProperties("When, in your time?", new TextInputProperties("when", TextInputStyle.Short)
            {
                Placeholder = "fri 20:00, tomorrow 19:30, 24.12 18:00",
                MaxLength = 50,
            }),
        });
}

// Suggestions: while it's voted on, another game (or, by Suggest a time, another time) or can't make any; once it's
// set, a change (another time and/or game), saying it works, and its planner making it.
public sealed class EventSuggestionButtons(EventBoard board, AccessControl access, IEnumerable<IEventKind> kinds) : GuildButtonModule
{
    [ComponentInteraction("eventsuggest")]
    public async Task<InteractionCallbackProperties> SuggestAsync(long eventId)
    {
        if (await board.FindAsync(GuildId, eventId) is not { } e)
            return InteractionCallback.Message(Replies.Ephemeral("It's gone."));
        var form = new ModalProperties($"eventsuggesttime:{eventId}", "Suggest a change")
        {
            new LabelProperties("Another time, in yours (optional)", new TextInputProperties("when", TextInputStyle.Short)
            {
                Placeholder = "sat 21:00, tomorrow 19:30, now",
                MaxLength = 50,
                Required = false,
            }),
        };
        if (Suggesting.GamesAllowed(e.Suggestions) && kinds.FirstOrDefault(k => k.Owns(e)) is { } kind
            && EventSuggestions.Menu("game", (await kind.GamesAsync(GuildId)).Where(g => g.GameId != e.GameId), required: false) is { } menu)
            form.Add(new LabelProperties("Another game (optional)", menu));
        return InteractionCallback.Modal(form);
    }

    [ComponentInteraction("eventsuggestgame")]
    public async Task<InteractionCallbackProperties> SuggestGameAsync(long eventId)
    {
        var e = await board.FindAsync(GuildId, eventId);
        var offered = e is null ? [] : (await board.GameOptionsAsync(eventId)).Select(o => o.GameId).Append(e.GameId ?? 0).ToHashSet();
        if (e is null || kinds.FirstOrDefault(k => k.Owns(e)) is not { } kind
            || EventSuggestions.Menu("game", (await kind.GamesAsync(GuildId)).Where(g => !offered.Contains(g.GameId)), required: true) is not { } menu)
            return InteractionCallback.Message(Replies.Ephemeral("There's no other game to suggest."));
        return InteractionCallback.Modal(new ModalProperties($"eventsuggestgameform:{eventId}", "Suggest a game") { new LabelProperties("Which game?", menu) });
    }

    [ComponentInteraction("eventcant")]
    public async Task CantAsync(long eventId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.CantMakeAsync(eventId, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = result);
    }

    // Agreeing can make the change, which posts and DMs.
    [ComponentInteraction("eventworks")]
    public async Task WorksAsync(long suggestionId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.WorksForAsync(suggestionId, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = result);
    }

    [ComponentInteraction("eventmoveto")]
    public async Task ChangeToAsync(long suggestionId)
    {
        var suggestion = await board.SuggestionAsync(suggestionId);
        var e = suggestion is null ? null : await board.FindAsync(GuildId, suggestion.EventId);
        if ((EventMoving.Refusal(e) ?? await EventMoving.NotTheirsAsync(e!, Guild, (GuildUser)Context.User, access)) is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.ChangeToAsync(suggestionId);
        await ModifyResponseAsync(m => m.Content = result);
    }
}

// Shared by the suggestion forms here and in group DMs.
public static class EventSuggestions
{
    // A dropdown of games for a form; null when there are none.
    public static StringMenuProperties? Menu(string id, IEnumerable<EventGameOption> games, bool required)
    {
        var options = games.Take(25).Select(g => new StringMenuSelectOptionProperties(g.Name.Length <= 100 ? g.Name : g.Name[..100], g.GameId.ToString())
        {
            Emoji = g.Emoji is { } emoji ? EmojiProperties.Standard(emoji) : null,
        }).ToList();
        return options.Count == 0 ? null : new StringMenuProperties(id, options) { MinValues = required ? 1 : 0, MaxValues = 1, Required = required };
    }

    public static string? Picked(IReadOnlyList<IComponent> components, string id)
        => components.OfType<Label>().Select(l => l.Component).OfType<StringMenu>().FirstOrDefault(m => m.CustomId == id)?.SelectedValues is { } values ? values.FirstOrDefault() : null;

    public static string Typed(IReadOnlyList<IComponent> components, string id)
        => components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().FirstOrDefault(t => t.CustomId == id)?.Value.Trim() ?? "";

    // The game as a change to a set session: the title it gives when the session's followed its game (else none).
    public static EventSuggestion Change(Event e, EventGameOption game, IReadOnlyList<EventGameOption> all)
        => new()
        {
            GameId = game.GameId,
            GameName = game.Name,
            Title = all.FirstOrDefault(g => g.GameId == e.GameId)?.Title == e.Title ? game.Title : null,
        };

    // The session's own game, as the first option when another is suggested (its title only when the session's followed it).
    public static EventGameOption? Current(Event e, IReadOnlyList<EventGameOption> all)
        => all.FirstOrDefault(g => g.GameId == e.GameId) is { } current
            ? new() { GameId = current.GameId, Name = current.Name, Emoji = current.Emoji, Players = current.Players, Title = current.Title == e.Title ? current.Title : null }
            : null;
}

// Who may move an event: whoever planned it, or someone with events.manage.
public static class EventMoving
{
    public static string? Refusal(Event? e) => e is { State: EventStates.Scheduled, StartsAt: not null } ? null : "There's no such event with its time set.";

    public static async Task<string?> NotTheirsAsync(Event e, Guild guild, GuildUser user, AccessControl access)
        => e.CreatorId == user.Id || await access.CanAsync(guild, user, BotPermissions.ManageEvents) ? null : $"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can move it.";

    // /event move and /session move: the move's answer, or why not.
    public static async Task<string> MoveAsync(EventBoard board, AccessControl access, TimeZones zones, TimeProvider time, Guild guild, GuildUser user, long eventId, string when)
    {
        var e = await board.FindAsync(guild.Id, eventId);
        var (zone, own) = await zones.ForAsync(guild.Id, user.Id);
        var parsed = WhenParser.Parse(when, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        return Refusal(e) ?? await NotTheirsAsync(e!, guild, user, access)
            ?? (parsed.At is { } at ? $"{await board.MoveAsync(e!.Id, at.ToDateTimeOffset())} {TimeZones.Hint(zone, own)}" : $"`{when}`: {parsed.Problem}\n{TimeZones.Hint(zone, own)}");
    }
}

public sealed class EventSuggestModal(EventBoard board, TimeZones zones, TimeProvider time, IEnumerable<IEventKind> kinds) : GuildModalModule
{
    // A change: another time and/or game.
    [ComponentInteraction("eventsuggesttime")]
    public async Task SuggestAsync(long eventId)
    {
        var typed = EventSuggestions.Typed(Context.Components, "when");
        var (zone, own) = await zones.ForAsync(GuildId, Context.User.Id);
        var when = typed.Length == 0 ? null : WhenParser.Parse(typed, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        if (when is { At: null })
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"{when.Problem}\n{TimeZones.Hint(zone, own)}")));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        EventSuggestion? game = null;
        if (await board.FindAsync(GuildId, eventId) is { } e && long.TryParse(EventSuggestions.Picked(Context.Components, "game"), out var gameId)
            && kinds.FirstOrDefault(k => k.Owns(e)) is { } kind && await kind.GamesAsync(GuildId) is var all && all.FirstOrDefault(g => g.GameId == gameId) is { } picked)
            game = EventSuggestions.Change(e, picked, all);
        var result = await board.SuggestAsync(eventId, Context.User.Id, when?.At?.ToDateTimeOffset(), game);
        await ModifyResponseAsync(m => m.Content = when is null ? result : $"{result} {TimeZones.Hint(zone, own)}");
    }

    // Another game to vote on.
    [ComponentInteraction("eventsuggestgameform")]
    public async Task SuggestGameAsync(long eventId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = "It's gone.";
        if (await board.FindAsync(GuildId, eventId) is { } e && long.TryParse(EventSuggestions.Picked(Context.Components, "game"), out var gameId)
            && kinds.FirstOrDefault(k => k.Owns(e)) is { } kind && await kind.GamesAsync(GuildId) is var all && all.FirstOrDefault(g => g.GameId == gameId) is { } picked)
            result = await board.AddGameAsync(eventId, Context.User.Id, picked, e.ChoosingGame ? null : EventSuggestions.Current(e, all));
        await ModifyResponseAsync(m => m.Content = result);
    }
}

public sealed class EventProposeModal(EventBoard board, TimeZones zones, TimeProvider time) : GuildModalModule
{
    [ComponentInteraction("eventproposetime")]
    public async Task ProposeAsync(long eventId)
    {
        var input = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        var (zone, own) = await zones.ForAsync(GuildId, Context.User.Id);
        var when = WhenParser.Parse(input, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        if (when.At is not { } at)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"{when.Problem}\n{TimeZones.Hint(zone, own)}")));
            return;
        }

        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.ProposeAsync(eventId, Context.User.Id, at.ToDateTimeOffset());
        await ModifyResponseAsync(m => m.Content = $"{result} {TimeZones.Hint(zone, own)}");
    }
}

public sealed class SeriesAutocomplete(IDbContextFactory<BotDbContext> dbFactory) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var guildId = context.Interaction.GuildId!.Value;
        await using var db = await dbFactory.CreateDbContextAsync();
        var series = await db.EventSeries.Where(s => s.GuildId == guildId && s.Active).ToListAsync();
        return series
            .Where(s => s.Title.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(s => (s.Id, Label: $"{s.Id}: {s.Title} ({Recurrence.Describe(s.Days)})"))
            .Select(s => new ApplicationCommandOptionChoiceProperties(s.Label.Length <= 100 ? s.Label : s.Label[..100], s.Id));
    }
}

public enum EventVoice
{
    None,
    Open,
    Locked,
}
