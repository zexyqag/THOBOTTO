using THOBOTTO.Discord;
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

    [Setting("Events last", Help = "Unless one is given a length of its own: how long it shows in calendars and in Discord's event list.", Unit = "minutes", Min = 15, Max = 1440)]
    public int UsualMinutes { get; init; } = 120;

    public TimeSpan LengthOf(Event e) => TimeSpan.FromMinutes(e.LengthMinutes ?? UsualMinutes);

    // An event's own length as typed ("90m", "2h", "1h30m"), 15 minutes to a day; none when nothing was typed.
    public static (int? Minutes, string? Problem) ReadLength(string? text)
        => string.IsNullOrWhiteSpace(text) ? (null, null)
            : Durations.Parse(text) is { } span && span >= TimeSpan.FromMinutes(15) && span <= TimeSpan.FromDays(1) ? ((int)span.TotalMinutes, null)
            : (null, "A length is like 90m, 2h or 1h30m, from 15 minutes to a day.");

    [Setting("Also list new events as Discord events", Help = "The default for new events; each can choose otherwise.")]
    public bool DiscordEvents { get; init; }

    [Setting("Take in events made in Discord's calendar", Help = "They become the bot's events in the happenings forum (when it shows events), with the people interested as coming, kept in step both ways.")]
    public bool ImportDiscordEvents { get; init; }

    [Setting("Category for event voice channels", Help = "None puts them in the event channel's category.", Kind = SettingKind.Category)]
    public ulong? VoiceCategoryId { get; init; }

    [Setting("Open voice channels", Unit = "minutes before the start", Min = 0, Max = 1440)]
    public int VoiceLeadMinutes { get; init; } = 15;
}
