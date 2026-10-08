using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Events;

[SlashCommand("event", "Plan events and see who's coming", Contexts = [InteractionContextType.Guild])]
public sealed class EventCommands(
    EventBoard board,
    TimeZones zones,
    ModuleState modules,
    SettingsStore settings,
    AccessControl access,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("plan", "Plan an event at a set time")]
    public async Task PlanAsync(
        [SlashCommandParameter(Description = "What's happening", MaxLength = 100)] string title,
        [SlashCommandParameter(Description = "When, in your time: 20:00, fri 8pm, tomorrow 19:30, 24.12 18:00, in 2h", MaxLength = 50)] string when,
        [SlashCommandParameter(Description = "More details", MaxLength = 1000)] string? description = null,
        [SlashCommandParameter(Description = "A role to ping about it")] Role? ping = null)
    {
        if (await RefusalAsync() is { } refusal)
        {
            await RespondAsync(InteractionCallback.Message(refusal));
            return;
        }

        var (zone, own) = await zones.ForAsync(GuildId, Context.User.Id);
        var parsed = WhenParser.Parse(when, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        if (parsed.At is not { } at)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"{parsed.Problem}\n{ZoneHint(zone, own)}")));
            return;
        }

        // Posting the event, pinging and DMing can take a moment.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var e = await board.CreateAsync(GuildId, Context.Channel.Id, Context.User.Id, title.Trim(), description?.Trim(), ping?.Id, at.ToDateTimeOffset());
        var unix = at.ToUnixTimeSeconds();
        await ModifyResponseAsync(m => m.Content = $"Event {e.Id} planned for <t:{unix}:F> (<t:{unix}:R>), read in {zone.Id}. {ZoneHint(zone, own)}");
    }

    [SubSlashCommand("cancel", "Call an event off")]
    public async Task<InteractionMessageProperties> CancelAsync(
        [SlashCommandParameter(Description = "Event", AutocompleteProviderType = typeof(EventAutocomplete))] long @event)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == @event && x.GuildId == GuildId);
        if (e is null || e.State != EventStates.Scheduled)
            return Replies.Ephemeral("There's no such upcoming event.");
        if (e.CreatorId != Context.User.Id && !await access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.ManageEvents))
            return Replies.Ephemeral($"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can cancel it.");

        await board.CancelAsync(e);
        return Replies.Ephemeral($"Cancelled **{e.Title}**.");
    }

    [SubSlashCommand("list", "Upcoming events")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var upcoming = await db.Events
            .Where(e => e.GuildId == GuildId && e.State == EventStates.Scheduled)
            .OrderBy(e => e.StartsAt)
            .Take(15)
            .ToListAsync();

        return Replies.Ephemeral(upcoming.Count == 0
            ? "Nothing planned. Start something with `/event plan`."
            : string.Join('\n', upcoming.Select(e => $"<t:{e.StartsAt.ToUnixTimeSeconds()}:f> **{e.Title}**" + (e.MessageId is { } m ? $" ([open](https://discord.com/channels/{GuildId}/{e.ChannelId}/{m}))" : ""))));
    }

    [SubSlashCommand("settings", "Server time zone, reminders, who may plan (needs events.manage)")]
    [RequirePermission(BotPermissions.ManageEvents)]
    public async Task<InteractionMessageProperties> SettingsAsync(
        [SlashCommandParameter(Name = "time-zone", Description = "For members who haven't set their own", AutocompleteProviderType = typeof(TimeZoneAutocomplete))] string? timeZone = null,
        [SlashCommandParameter(Name = "reminder-minutes", Description = "Remind attendees this long before; 0 for none", MinValue = 0, MaxValue = 10080)] int? reminderMinutes = null,
        [SlashCommandParameter(Name = "create-needs-permission", Description = "Only members with events.create may plan")] bool? createNeedsPermission = null,
        [SlashCommandParameter(Name = "end-after-hours", Description = "RSVPs close this long after the start", MinValue = 1, MaxValue = 168)] int? endAfterHours = null)
    {
        if (timeZone is not null && TimeZones.Find(timeZone) is null)
            return Replies.Ephemeral($"`{timeZone}` isn't a time zone I know. Pick one from the list.");

        var before = await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId);
        var after = before with
        {
            TimeZone = timeZone ?? before.TimeZone,
            ReminderMinutes = reminderMinutes ?? before.ReminderMinutes,
            CreateNeedsPermission = createNeedsPermission ?? before.CreateNeedsPermission,
            EndAfterHours = endAfterHours ?? before.EndAfterHours,
        };
        var changed = after != before;
        if (changed)
            await settings.SetAsync(GuildId, EventBoard.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Server time zone: {after.TimeZone}
            Reminders: {(after.ReminderMinutes == 0 ? "off" : $"{after.ReminderMinutes} min before")}
            Planning: {(after.CreateNeedsPermission ? $"needs `{BotPermissions.CreateEvents}`" : "anyone")}
            RSVPs close {after.EndAfterHours} h after the start.
            """);
    }

    private async Task<InteractionMessageProperties?> RefusalAsync()
    {
        if (!await modules.IsEnabledAsync(GuildId, EventBoard.ModuleId))
            return Replies.Ephemeral($"The `{EventBoard.ModuleId}` module is off.");
        var rules = await settings.GetAsync<EventRules>(GuildId, EventBoard.ModuleId);
        if (rules.CreateNeedsPermission && !await access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.CreateEvents))
            return Replies.Ephemeral($"Planning events needs `{BotPermissions.CreateEvents}` here.");
        return null;
    }

    internal static string ZoneHint(DateTimeZone zone, bool own)
        => own ? "" : $"(That's the server's time zone, {zone.Id}. Set your own with `/timezone set` if you're elsewhere.)";
}

[SlashCommand("timezone", "Your time zone, for reading the times you type", Contexts = [InteractionContextType.Guild])]
public sealed class TimeZoneCommands(TimeZones zones, TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("set", "Set your time zone")]
    public async Task<InteractionMessageProperties> SetAsync(
        [SlashCommandParameter(Description = "Start typing a city, e.g. Copenhagen", AutocompleteProviderType = typeof(TimeZoneAutocomplete))] string zone)
    {
        if (TimeZones.Find(zone) is not { } found)
            return Replies.Ephemeral($"`{zone}` isn't a time zone I know. Pick one from the list.");

        await zones.SetAsync(Context.User.Id, found.Id);
        return Replies.Ephemeral($"Your time zone is now {found.Id}; it's {Now(found)} there.");
    }

    [SubSlashCommand("show", "Which time zone the bot reads your times in")]
    public async Task<InteractionMessageProperties> ShowAsync()
    {
        var (zone, own) = await zones.ForAsync(Context.Guild!.Id, Context.User.Id);
        return Replies.Ephemeral($"I read your times in {zone.Id} (it's {Now(zone)} there). {EventCommands.ZoneHint(zone, own)}");
    }

    private string Now(DateTimeZone zone) => Instant.FromDateTimeOffset(time.GetUtcNow()).InZone(zone).ToString("HH:mm, ddd d MMM", null);
}

public sealed class EventButtons(EventBoard board) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("event")]
    public async Task RsvpAsync(long eventId, string status)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await board.RsvpAsync(eventId, Context.User.Id, status);
        await ModifyResponseAsync(m => m.Content = result);
    }
}

public sealed class TimeZoneAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = (option.Value ?? "").Replace(' ', '_');
        var choices = TimeZones.Ids
            .Where(id => id.Contains('/') && id.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => !id.Split('/')[^1].StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ThenBy(id => id)
            .Take(25)
            .Select(id => new ApplicationCommandOptionChoiceProperties(id, id));
        return new(choices);
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
        var events = await db.Events.Where(e => e.GuildId == guildId && e.State == EventStates.Scheduled).OrderBy(e => e.StartsAt).Take(100).ToListAsync();
        return events
            .Where(e => e.Title.Contains(input, StringComparison.OrdinalIgnoreCase) || e.Id.ToString() == input)
            .Take(25)
            .Select(e => (e.Id, Label: $"{e.Id}: {e.Title} ({e.StartsAt:yyyy-MM-dd HH:mm} UTC)"))
            .Select(e => new ApplicationCommandOptionChoiceProperties(e.Label.Length <= 100 ? e.Label : e.Label[..100], e.Id));
    }
}
