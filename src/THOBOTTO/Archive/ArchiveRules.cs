using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

// Stored with SettingsStore under the module id.
public sealed record ArchiveRules
{
    [Setting("Save attachment files", Help = "Only when storage is configured for the bot; otherwise only their details are kept.")]
    public bool SaveAttachments { get; init; } = true;

    [Setting("Largest file to save", Help = "Larger ones are recorded but not downloaded. Empty keeps everything.", Kind = SettingKind.Megabytes, Unit = "MB", Min = 1, Max = 100_000)]
    public long? MaxAttachmentBytes { get; init; }

    [Setting("Channels not archived", Kind = SettingKind.TextChannel)]
    public IReadOnlyList<ulong> ExcludedChannelIds { get; init; } = [];
}
