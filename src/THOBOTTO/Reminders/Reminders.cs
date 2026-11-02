using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Events;
using THOBOTTO.Members;

namespace THOBOTTO.Reminders;

// A DM about a message at a time someone chose: a note (the message's text, unless they wrote their own) and a link.
public sealed class Reminder
{
    public long Id { get; init; }

    public ulong UserId { get; init; }

    // Zero for a message in a DM or group DM.
    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong MessageId { get; init; }

    public required string Note { get; init; }

    public DateTimeOffset At { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ReminderBook(IDbContextFactory<BotDbContext> dbFactory, RestClient rest, TimeProvider time, ILogger<ReminderBook> logger) : BackgroundService
{
    public const int NoteLength = 200;
    private const int MostOpen = 25;

    public async Task<string?> AddAsync(ulong userId, ulong guildId, ulong channelId, ulong messageId, string note, DateTimeOffset at)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Reminders.CountAsync(r => r.UserId == userId) >= MostOpen)
            return $"You already have {MostOpen} reminders waiting.";
        db.Reminders.Add(new() { UserId = userId, GuildId = guildId, ChannelId = channelId, MessageId = messageId, Note = note, At = at, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        return null;
    }

    public static string Link(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{(guildId == 0 ? "@me" : guildId.ToString())}/{channelId}/{messageId}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                foreach (var due in await db.Reminders.Where(r => r.At <= time.GetUtcNow()).ToListAsync(ct))
                {
                    try
                    {
                        var dm = await rest.GetDMChannelAsync(due.UserId, cancellationToken: ct);
                        await dm.SendMessageAsync(new() { Content = $"⏰ {due.Note}\n{Link(due.GuildId, due.ChannelId, due.MessageId)}" }, cancellationToken: ct);
                    }
                    catch (RestException ex)
                    {
                        logger.LogInformation("Reminding {UserId} failed: {Message}", due.UserId, ex.Message);
                    }
                    db.Reminders.Remove(due);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sending reminders failed");
            }
        }
    }
}

// Right-click a message, Apps, Remind me: in a server, and in DMs and group DMs for those who added the bot.
public sealed class RemindCommand : ApplicationCommandModule<ApplicationCommandContext>
{
    [MessageCommand("Remind me", IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
        Contexts = [InteractionContextType.Guild, InteractionContextType.BotDMChannel, InteractionContextType.DMChannel])]
    public InteractionCallbackProperties RemindAsync(RestMessage message)
    {
        var text = string.IsNullOrWhiteSpace(message.Content) ? "A message you wanted to come back to" : message.Content.Trim();
        return InteractionCallback.Modal(new ModalProperties($"remindme:{Context.Interaction.GuildId ?? 0}:{message.ChannelId}:{message.Id}", "Remind me")
        {
            new LabelProperties("When? In your time", new TextInputProperties("when", TextInputStyle.Short) { Placeholder = "in 2h, tomorrow 9:00, fri 18:00", MaxLength = 50 }),
            new LabelProperties("About", new TextInputProperties("note", TextInputStyle.Paragraph)
            {
                Value = text.Length <= ReminderBook.NoteLength ? text : text[..(ReminderBook.NoteLength - 1)] + "…",
                MaxLength = ReminderBook.NoteLength,
            }),
        });
    }
}

public sealed class RemindModal(ReminderBook book, TimeZones zones, TimeProvider time) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("remindme")]
    public async Task<InteractionCallbackProperties> RemindAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        var inputs = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());
        var (zone, own) = await zones.ForAsync(guildId, Context.User.Id);
        // Outside a server there's no server's zone to fall back on.
        var hint = guildId == 0 && !own ? "(Read as UTC: set your own time zone with `/me timezone` in a server with the bot.)" : TimeZones.Hint(zone, own);
        var when = WhenParser.Parse(inputs.GetValueOrDefault("when") ?? "", zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        if (when.At is not { } at)
            return InteractionCallback.Message(Replies.Ephemeral($"{when.Problem}\n{hint}"));
        var note = inputs.GetValueOrDefault("note") is { Length: > 0 } written ? written : "A message you wanted to come back to";
        var problem = await book.AddAsync(Context.User.Id, guildId, channelId, messageId, note, at.ToDateTimeOffset());
        return InteractionCallback.Message(Replies.Ephemeral(problem ?? $"I'll DM you <t:{at.ToUnixTimeSeconds()}:F> (<t:{at.ToUnixTimeSeconds()}:R>). {hint}"));
    }
}

// The reminders a member has waiting about a server's messages.
public sealed class RemindersMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Reminders";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Reminders.LongCountAsync(r => r.GuildId == guildId && r.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var r in db.Reminders.AsNoTracking().Where(r => r.GuildId == guildId && r.UserId == userId).OrderBy(r => r.At).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("at", r.At);
            json.WriteString("note", r.Note);
            json.WriteString("message", ReminderBook.Link(r.GuildId, r.ChannelId, r.MessageId));
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}

public static class RemindersStartup
{
    public static IServiceCollection AddReminders(this IServiceCollection services)
        => services
            .AddSingleton<ReminderBook>()
            .AddHostedService(s => s.GetRequiredService<ReminderBook>())
            .AddSingleton<IMemberData, RemindersMemberData>();
}
