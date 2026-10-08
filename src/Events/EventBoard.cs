using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Events;

// Events, their RSVPs, and polls on when to hold them. A sweep every minute decides polls whose
// deadline passed, sends reminders and the "starting now" ping, and closes RSVPs once an event
// is well past its start. All changes run one at a time under a gate.
public sealed class EventBoard(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    Notifier notifier,
    TimeProvider time,
    ILogger<EventBoard> logger) : BackgroundService
{
    public const string ModuleId = "events";
    public const int MaxTimeOptions = 10;

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<Event> CreateAsync(ulong guildId, ulong channelId, ulong creatorId, string title, string? description, ulong? pingRoleId, DateTimeOffset startsAt)
        => AddAsync(new()
        {
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
        IReadOnlyList<DateTimeOffset> times, DateTimeOffset closesAt, bool allowProposals)
        => AddAsync(new()
        {
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

    public async Task<string> RsvpAsync(long eventId, ulong userId, string status)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is null || e.State != EventStates.Scheduled || e.StartsAt is null)
                return "RSVPs for this event are closed.";

            await SetRsvpAsync(db, eventId, userId, status);
            await db.SaveChangesAsync();
            await RenderAsync(db, e);

            return status switch
            {
                RsvpStatuses.In => "You're in. 🎉",
                RsvpStatuses.Maybe => "Marked as maybe.",
                _ => "Marked as not coming.",
            };
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
            await RenderAsync(db, e);
            return $"Added <t:{startsAt.ToUnixTimeSeconds()}:F>, with your vote.";
        }
        finally
        {
            _gate.Release();
        }
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

            var involved = (await Attendees(db, e.Id)).Concat(await Voters(db, e.Id)).Distinct();
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
            db.Events.Add(e);
            await db.SaveChangesAsync();

            if (e.StartsAt is not null && e.SeriesId is null)
                // The creator of a one-off is going, presumably; a series doesn't sign them up every time.
                db.EventRsvps.Add(new() { EventId = e.Id, UserId = e.CreatorId, Status = RsvpStatuses.In, At = e.CreatedAt });
            foreach (var at in times.Distinct().Order())
                db.EventTimeOptions.Add(new() { EventId = e.Id, StartsAt = at, ProposedById = e.CreatorId, CreatedAt = e.CreatedAt });
            await db.SaveChangesAsync();

            var (embed, components) = await BuildAsync(db, e);
            var message = await rest.SendMessageAsync(e.ChannelId, new()
            {
                Content = e.PingRoleId is { } role ? $"<@&{role}>" : null,
                Embeds = [embed],
                Components = components,
                AllowedMentions = new() { AllowedRoles = e.PingRoleId is { } r ? [r] : [], AllowedUsers = [] },
            });
            e.MessageId = message.Id;
            await db.SaveChangesAsync();

            var when = e.StartsAt is { } s ? $"<t:{s.ToUnixTimeSeconds()}:F>" : "time to be voted on";
            await notifier.NotifySubscribersAsync(e.GuildId, NotificationTopics.EventsNew, $"new event **{e.Title}**, {when}", Link(e));
            return e;
        }
        finally
        {
            _gate.Release();
        }
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
        var going = votes.Where(v => v.OptionId == winner.Id).Select(v => v.UserId).ToList();
        foreach (var userId in going)
            await SetRsvpAsync(db, e.Id, userId, RsvpStatuses.In);
        await db.SaveChangesAsync();
        await RenderAsync(db, e);

        var at = winner.StartsAt.ToUnixTimeSeconds();
        var everyone = votes.Select(v => v.UserId).Distinct().ToList();
        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, everyone, $"**{e.Title}** will be <t:{at}:F>", Link(e));
        await PostAsync(e, $"**{e.Title}** is on: <t:{at}:F> (<t:{at}:R>). Marked as in: {(going.Count == 0 ? "nobody yet" : string.Join(' ', going.Select(id => $"<@{id}>")))}.", going);
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

            foreach (var poll in await db.Events.Where(e => e.State == EventStates.Scheduled && e.StartsAt == null && e.PollClosesAt <= now).ToListAsync(ct))
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
                }

                if (!e.StartSent && start <= now)
                {
                    e.StartSent = true;
                    e.ReminderSent = true;
                    await db.SaveChangesAsync(ct);
                    await AnnounceAsync(db, e, $"**{e.Title}** is starting now!");
                }

                if (start + TimeSpan.FromHours(rules.EndAfterHours) <= now)
                {
                    e.State = EventStates.Over;
                    await db.SaveChangesAsync(ct);
                    await RenderAsync(db, e);
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
        var going = await Attendees(db, e.Id);
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
            logger.LogWarning("Couldn't post about event {EventId}: {Message}", e.Id, ex.Message);
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

    private static async Task<List<ulong>> Attendees(BotDbContext db, long eventId)
        => await db.EventRsvps.Where(r => r.EventId == eventId && r.Status == RsvpStatuses.In).Select(r => r.UserId).ToListAsync();

    private static async Task<List<ulong>> Voters(BotDbContext db, long eventId)
        => await db.EventTimeVotes.Where(v => db.EventTimeOptions.Any(o => o.Id == v.OptionId && o.EventId == eventId)).Select(v => v.UserId).Distinct().ToListAsync();

    private async Task RenderAsync(BotDbContext db, Event e)
    {
        if (e.MessageId is not { } messageId)
            return;
        var (embed, components) = await BuildAsync(db, e);
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

    private static async Task<(EmbedProperties Embed, IEnumerable<IMessageComponentProperties> Components)> BuildAsync(BotDbContext db, Event e)
    {
        var text = new StringBuilder();
        var open = e.State == EventStates.Scheduled;
        IEnumerable<IMessageComponentProperties> components;

        if (e.StartsAt is null && e.State != EventStates.Cancelled)
        {
            var options = await db.EventTimeOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.StartsAt).ToListAsync();
            var ids = options.Select(o => o.Id).ToList();
            var votes = await db.EventTimeVotes.Where(v => ids.Contains(v.OptionId)).ToListAsync();

            text.AppendLine($"**When can you make it?** Vote for every time that works. Voting ends <t:{e.PollClosesAt!.Value.ToUnixTimeSeconds()}:R>.");
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
            if (e.AllowProposals && open && options.Count < MaxTimeOptions)
                rows.Add(new ActionRowProperties { new ButtonProperties($"eventpropose:{e.Id}", "Propose a time", EmojiProperties.Standard("➕"), ButtonStyle.Secondary) });
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
            });
            if (e.Description is { } description)
                text.AppendLine().AppendLine(description);
            text.AppendLine();
            text.AppendLine($"✅ **In** ({rsvps.Count(r => r.Status == RsvpStatuses.In)}): {Names(RsvpStatuses.In)}");
            text.AppendLine($"🤔 **Maybe**: {Names(RsvpStatuses.Maybe)}");
            text.AppendLine($"❌ **Out**: {Names(RsvpStatuses.Out)}");

            components = [new ActionRowProperties
            {
                new ButtonProperties($"event:{e.Id}:{RsvpStatuses.In}", "In", EmojiProperties.Standard("✅"), ButtonStyle.Success) { Disabled = !open },
                new ButtonProperties($"event:{e.Id}:{RsvpStatuses.Maybe}", "Maybe", EmojiProperties.Standard("🤔"), ButtonStyle.Secondary) { Disabled = !open },
                new ButtonProperties($"event:{e.Id}:{RsvpStatuses.Out}", "Out", EmojiProperties.Standard("❌"), ButtonStyle.Secondary) { Disabled = !open },
            }];
        }

        text.Append($"-# Event {e.Id} by <@{e.CreatorId}> · times show in your own time zone");
        var embed = new EmbedProperties
        {
            Title = e.Title,
            Description = text.ToString(),
            Color = open ? new(0x5865F2) : new(0x99AAB5),
        };
        return (embed, components);
    }

    private static string? Link(Event e) => e.MessageId is { } m ? Notifier.Link(e.GuildId, e.ChannelId, m) : null;
}
