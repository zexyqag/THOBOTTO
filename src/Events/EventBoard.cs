using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Events;

// Events and their RSVPs. A sweep every minute sends reminders, the "starting now" ping, and
// closes RSVPs once an event is well past its start.
public sealed class EventBoard(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    Notifier notifier,
    TimeProvider time,
    ILogger<EventBoard> logger) : BackgroundService
{
    public const string ModuleId = "events";

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Event> CreateAsync(ulong guildId, ulong channelId, ulong creatorId, string title, string? description, ulong? pingRoleId, DateTimeOffset startsAt)
    {
        var e = new Event
        {
            GuildId = guildId,
            ChannelId = channelId,
            CreatorId = creatorId,
            Title = title,
            Description = description,
            PingRoleId = pingRoleId,
            StartsAt = startsAt,
            CreatedAt = time.GetUtcNow(),
        };

        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Events.Add(e);
            await db.SaveChangesAsync();

            // The creator is going, presumably.
            db.EventRsvps.Add(new() { EventId = e.Id, UserId = creatorId, Status = RsvpStatuses.In, At = e.CreatedAt });
            await db.SaveChangesAsync();

            var message = await rest.SendMessageAsync(channelId, new()
            {
                Content = pingRoleId is { } role ? $"<@&{role}>" : null,
                Embeds = [await EmbedAsync(db, e)],
                Components = [Buttons(e, open: true)],
                AllowedMentions = new() { AllowedRoles = pingRoleId is { } r ? [r] : [], AllowedUsers = [] },
            });
            e.MessageId = message.Id;
            await db.SaveChangesAsync();

            await notifier.NotifySubscribersAsync(guildId, NotificationTopics.EventsNew,
                $"new event **{title}** <t:{startsAt.ToUnixTimeSeconds()}:F>", Notifier.Link(guildId, channelId, message.Id));
            return e;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RsvpAsync(long eventId, ulong userId, string status)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.Events.FindAsync(eventId);
            if (e is null || e.State != EventStates.Scheduled)
                return "RSVPs for this event are closed.";

            var rsvp = await db.EventRsvps.FindAsync(eventId, userId);
            if (rsvp is null)
                db.EventRsvps.Add(new() { EventId = eventId, UserId = userId, Status = status, At = time.GetUtcNow() });
            else
            {
                rsvp.Status = status;
                rsvp.At = time.GetUtcNow();
            }
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

            var going = await Attendees(db, e.Id);
            await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, going, $"**{e.Title}** was cancelled", Link(e));
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

    private async Task SweepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var due = await db.Events.Where(e => e.State == EventStates.Scheduled && e.StartsAt <= now + TimeSpan.FromDays(1)).ToListAsync(ct);

            foreach (var e in due)
            {
                var rules = await settings.GetAsync<EventRules>(e.GuildId, ModuleId);

                if (!e.ReminderSent && rules.ReminderMinutes > 0 && e.StartsAt - now <= TimeSpan.FromMinutes(rules.ReminderMinutes) && e.StartsAt > now)
                {
                    e.ReminderSent = true;
                    await db.SaveChangesAsync(ct);
                    await AnnounceAsync(db, e, $"**{e.Title}** starts <t:{e.StartsAt.ToUnixTimeSeconds()}:R>.");
                }

                if (!e.StartSent && e.StartsAt <= now)
                {
                    e.StartSent = true;
                    e.ReminderSent = true;
                    await db.SaveChangesAsync(ct);
                    await AnnounceAsync(db, e, $"**{e.Title}** is starting now!");
                }

                if (e.StartsAt + TimeSpan.FromHours(rules.EndAfterHours) <= now)
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
        {
            try
            {
                await rest.SendMessageAsync(e.ChannelId, new()
                {
                    Content = $"{text} {string.Join(' ', going.Select(id => $"<@{id}>"))}",
                    MessageReference = e.MessageId is { } m ? MessageReferenceProperties.Reply(m, failIfNotExists: false) : null,
                    AllowedMentions = new() { AllowedUsers = going, ReplyMention = false },
                });
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                logger.LogWarning("Couldn't announce event {EventId}: {Message}", e.Id, ex.Message);
            }
        }

        await notifier.NotifyAsync(e.GuildId, NotificationTopics.EventsReminder, going, text, Link(e));
    }

    private static async Task<List<ulong>> Attendees(BotDbContext db, long eventId)
        => await db.EventRsvps.Where(r => r.EventId == eventId && r.Status == RsvpStatuses.In).Select(r => r.UserId).ToListAsync();

    private async Task RenderAsync(BotDbContext db, Event e)
    {
        if (e.MessageId is not { } messageId)
            return;
        var embed = await EmbedAsync(db, e);
        try
        {
            await rest.ModifyMessageAsync(e.ChannelId, messageId, m =>
            {
                m.Embeds = [embed];
                m.Components = [Buttons(e, open: e.State == EventStates.Scheduled)];
            });
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task<EmbedProperties> EmbedAsync(BotDbContext db, Event e)
    {
        var rsvps = await db.EventRsvps.Where(r => r.EventId == e.Id).OrderBy(r => r.At).ToListAsync();
        string Names(string status)
        {
            var who = rsvps.Where(r => r.Status == status).Select(r => $"<@{r.UserId}>").ToList();
            return who.Count == 0 ? "–" : string.Join(", ", who);
        }

        var text = new StringBuilder();
        var start = e.StartsAt.ToUnixTimeSeconds();
        text.AppendLine(e.State switch
        {
            EventStates.Cancelled => $"~~<t:{start}:F>~~ **Cancelled.**",
            EventStates.Over => $"<t:{start}:F> (over)",
            _ => $"<t:{start}:F> (<t:{start}:R>)",
        });
        if (e.Description is { } description)
            text.AppendLine().AppendLine(description);
        text.AppendLine();
        text.AppendLine($"✅ **In** ({rsvps.Count(r => r.Status == RsvpStatuses.In)}): {Names(RsvpStatuses.In)}");
        text.AppendLine($"🤔 **Maybe**: {Names(RsvpStatuses.Maybe)}");
        text.AppendLine($"❌ **Out**: {Names(RsvpStatuses.Out)}");
        text.Append($"-# Event {e.Id} by <@{e.CreatorId}> · times show in your own time zone");

        return new()
        {
            Title = e.Title,
            Description = text.ToString(),
            Color = e.State == EventStates.Scheduled ? new(0x5865F2) : new(0x99AAB5),
        };
    }

    private static ActionRowProperties Buttons(Event e, bool open) => new()
    {
        new ButtonProperties($"event:{e.Id}:{RsvpStatuses.In}", "In", EmojiProperties.Standard("✅"), ButtonStyle.Success) { Disabled = !open },
        new ButtonProperties($"event:{e.Id}:{RsvpStatuses.Maybe}", "Maybe", EmojiProperties.Standard("🤔"), ButtonStyle.Secondary) { Disabled = !open },
        new ButtonProperties($"event:{e.Id}:{RsvpStatuses.Out}", "Out", EmojiProperties.Standard("❌"), ButtonStyle.Secondary) { Disabled = !open },
    };

    private static string? Link(Event e) => e.MessageId is { } m ? Notifier.Link(e.GuildId, e.ChannelId, m) : null;
}
