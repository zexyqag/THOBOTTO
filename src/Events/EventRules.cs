using THOBOTTO.Modules;

namespace THOBOTTO.Events;

// Stored with SettingsStore under the module id.
public sealed record EventRules
{
    [Setting("Server time zone", Help = "For members who haven't set their own with /me timezone.", Kind = SettingKind.TimeZone)]
    public string TimeZone { get; init; } = "UTC";

    [Setting("Remind attendees", Help = "This long before the start; 0 turns reminders off.", Unit = "minutes before", Min = 0, Max = 10080)]
    public int ReminderMinutes { get; init; } = 30;

    [Setting("Planning needs a permission", Help = "Only members with events.create may plan events.")]
    public bool CreateNeedsPermission { get; init; }

    [Setting("Close RSVPs", Unit = "hours after the start", Min = 1, Max = 168)]
    public int EndAfterHours { get; init; } = 6;

    [Setting("Also list new events as Discord events", Help = "The default for new events; each can choose otherwise.")]
    public bool DiscordEvents { get; init; }

    [Setting("Category for event voice channels", Help = "None puts them in the event channel's category.", Kind = SettingKind.Category)]
    public ulong? VoiceCategoryId { get; init; }

    [Setting("Open voice channels", Unit = "minutes before the start", Min = 0, Max = 1440)]
    public int VoiceLeadMinutes { get; init; } = 15;
}
