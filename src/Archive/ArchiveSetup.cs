
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("archive", "The archive: attachments, excluded channels (needs archive.manage)")]
    [RequirePermission(BotPermissions.ManageArchive)]
    public async Task<InteractionMessageProperties> ArchiveAsync(
        [SlashCommandParameter(Name = "save-attachments", Description = "Download attachment files (when storage is configured)")] bool? saveAttachments = null,
        [SlashCommandParameter(Name = "max-attachment-mb", Description = "Larger files are recorded but not downloaded; 0 means no limit", MinValue = 0, MaxValue = 100_000)] int? maxMb = null,
        [SlashCommandParameter(Description = "A channel to stop archiving")] Channel? exclude = null,
        [SlashCommandParameter(Description = "An excluded channel to archive again")] Channel? include = null)
    {
        var before = await Get<SettingsStore>().GetAsync<ArchiveRules>(GuildId, Archiver.ModuleId);
        var excluded = before.ExcludedChannelIds.Where(id => id != include?.Id).ToList();
        if (exclude is not null && !excluded.Contains(exclude.Id))
            excluded.Add(exclude.Id);

        var after = before with
        {
            SaveAttachments = saveAttachments ?? before.SaveAttachments,
            MaxAttachmentBytes = maxMb is null ? before.MaxAttachmentBytes : maxMb == 0 ? null : maxMb * 1_048_576L,
            ExcludedChannelIds = excluded,
        };

        var changed = !SettingsStore.Same(before, after);
        if (changed)
            await Get<SettingsStore>().SetAsync(GuildId, Archiver.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        var store = Get<IAttachmentStore>();
        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")} `/archive status` shows what's archived.
            Attachment files: {(store.Enabled ? (after.SaveAttachments ? $"saved{(after.MaxAttachmentBytes is { } max ? $" up to {max / 1_048_576} MB" : "")}" : "not saved (turned off)") : "not saved (no storage configured for the bot)")}.
            Excluded channels: {(after.ExcludedChannelIds.Count == 0 ? "none" : string.Join(' ', after.ExcludedChannelIds.Select(id => $"<#{id}>")))}
            """);
    }
}
