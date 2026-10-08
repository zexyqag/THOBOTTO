namespace THOBOTTO.Events;

// Stored with SettingsStore under the module id.
public sealed record EventRules
{
    // IANA id; used for members who haven't set their own. UTC until an admin sets it.
    public string TimeZone { get; init; } = "UTC";

    // Attendees are reminded this long before the start; 0 turns reminders off.
    public int ReminderMinutes { get; init; } = 30;

    // When on, planning events needs the events.create permission.
    public bool CreateNeedsPermission { get; init; }

    // RSVPs close this long after the start.
    public int EndAfterHours { get; init; } = 6;

    // The default for new events: also create a Discord scheduled event.
    public bool DiscordEvents { get; init; }

    // Where event voice channels go; null puts them in the event channel's category.
    public ulong? VoiceCategoryId { get; init; }

    // Event voice channels open this long before the start.
    public int VoiceLeadMinutes { get; init; } = 15;
}
