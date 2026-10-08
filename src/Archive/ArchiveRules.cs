namespace THOBOTTO.Archive;

// Stored with SettingsStore under the module id.
public sealed record ArchiveRules
{
    // Whether attachments are downloaded (when storage is configured at all).
    public bool SaveAttachments { get; init; } = true;

    // Larger attachments are recorded but not downloaded. Null keeps everything.
    public long? MaxAttachmentBytes { get; init; }

    public IReadOnlyList<ulong> ExcludedChannelIds { get; init; } = [];
}
