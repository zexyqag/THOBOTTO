
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Events;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("events", "Events: time zone, reminders, who may plan, voice, Discord events (needs events.manage)")]
    [RequirePermission(BotPermissions.ManageEvents)]
    public async Task<InteractionMessageProperties> EventsAsync(
        [SlashCommandParameter(Name = "time-zone", Description = "For members who haven't set their own", AutocompleteProviderType = typeof(TimeZoneAutocomplete))] string? timeZone = null,
        [SlashCommandParameter(Name = "reminder-minutes", Description = "Remind attendees this long before; 0 for none", MinValue = 0, MaxValue = 10080)] int? reminderMinutes = null,
        [SlashCommandParameter(Name = "create-needs-permission", Description = "Only members with events.create may plan")] bool? createNeedsPermission = null,
        [SlashCommandParameter(Name = "end-after-hours", Description = "RSVPs close this long after the start", MinValue = 1, MaxValue = 168)] int? endAfterHours = null,
        [SlashCommandParameter(Name = "discord-events", Description = "By default, also list events in the server's Discord events")] bool? discordEvents = null,
        [SlashCommandParameter(Name = "voice-category", Description = "Category for event voice channels (default: the event channel's)", AllowedChannelTypes = [ChannelType.CategoryChannel])] Channel? voiceCategory = null,
        [SlashCommandParameter(Name = "voice-lead-minutes", Description = "Voice channels open this long before the start", MinValue = 0, MaxValue = 1440)] int? voiceLeadMinutes = null)
    {
        if (timeZone is not null && TimeZones.Find(timeZone) is null)
            return Replies.Ephemeral($"`{timeZone}` isn't a time zone I know. Pick one from the list.");

        var before = await Get<SettingsStore>().GetAsync<EventRules>(GuildId, EventBoard.ModuleId);
        var after = before with
        {
            TimeZone = timeZone ?? before.TimeZone,
            ReminderMinutes = reminderMinutes ?? before.ReminderMinutes,
            CreateNeedsPermission = createNeedsPermission ?? before.CreateNeedsPermission,
            EndAfterHours = endAfterHours ?? before.EndAfterHours,
            DiscordEvents = discordEvents ?? before.DiscordEvents,
            VoiceCategoryId = voiceCategory?.Id ?? before.VoiceCategoryId,
            VoiceLeadMinutes = voiceLeadMinutes ?? before.VoiceLeadMinutes,
        };
        var changed = after != before;
        if (changed)
            await Get<SettingsStore>().SetAsync(GuildId, EventBoard.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Server time zone: {after.TimeZone}
            Reminders: {(after.ReminderMinutes == 0 ? "off" : $"{after.ReminderMinutes} min before")}
            Planning: {(after.CreateNeedsPermission ? $"needs `{BotPermissions.CreateEvents}`" : "anyone")}
            RSVPs close {after.EndAfterHours} h after the start.
            Discord events: {(after.DiscordEvents ? "on by default" : "off by default")}
            Voice channels: open {after.VoiceLeadMinutes} min before, in {(after.VoiceCategoryId is { } cat ? $"<#{cat}>" : "the event channel's category")}
            """);
    }
}
