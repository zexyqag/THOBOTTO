using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

[SlashCommand("archive", "The message archive", Contexts = [InteractionContextType.Guild])]
[RequirePermission(BotPermissions.ManageArchive)]
public sealed class ArchiveCommands(IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store, SettingsStore settings, ModuleState modules, Backfiller backfiller)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("status", "What has been archived so far")]
    public async Task<InteractionMessageProperties> StatusAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var messages = db.ArchivedMessages.Where(m => m.GuildId == GuildId);
        var total = await messages.CountAsync();
        var deleted = await messages.CountAsync(m => m.DeletedAt != null);
        var edited = await db.MessageVersions.CountAsync(v => messages.Any(m => m.Id == v.MessageId));
        var attachments = db.ArchivedAttachments.Where(a => messages.Any(m => m.Id == a.MessageId));
        var attachmentCount = await attachments.CountAsync();
        var stored = await attachments.Where(a => a.StoredKey != null).ToListAsync();
        var rules = await settings.GetAsync<ArchiveRules>(GuildId, Archiver.ModuleId);
        var on = await modules.IsEnabledAsync(GuildId, Archiver.ModuleId);
        var run = await db.BackfillRuns.FindAsync(GuildId);
        var channels = await db.BackfillChannels.Where(c => c.GuildId == GuildId).ToListAsync();
        var backfill = run is null
            ? "not started (`/archive backfill start`)"
            : $"{(run.Running ? "running" : run.FinishedAt is { } done ? $"finished <t:{done.ToUnixTimeSeconds()}:R>" : "paused")}: {channels.Count(c => c.Done)}/{channels.Count} channels and threads done, {channels.Sum(c => c.Fetched)} messages fetched"
                + (channels.Any(c => c.Problem is not null) ? $"; skipped: {string.Join(", ", channels.Where(c => c.Problem is not null).Select(c => $"#{c.Name} ({c.Problem})"))}" : "");

        return Replies.Ephemeral($"""
            Archiving is {(on ? "on" : $"off (`/modules enable {Archiver.ModuleId}`)")}.
            {Count(total, "message")}, {deleted} of them deleted, {Count(edited, "earlier version")} from edits.
            {Count(attachmentCount, "attachment")}, {stored.Count} stored ({Size(stored.Sum(a => a.Size))}).
            Attachment files: {(store.Enabled ? (rules.SaveAttachments ? $"saved{(rules.MaxAttachmentBytes is { } max ? $" up to {max / 1_048_576} MB" : "")}" : "not saved (turned off)") : "not saved (no storage configured for the bot)")}.
            Backfill: {backfill}
            Excluded channels: {(rules.ExcludedChannelIds.Count == 0 ? "none" : string.Join(' ', rules.ExcludedChannelIds.Select(id => $"<#{id}>")))}
            """);
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1_048_576.0:0.#} MB",
        _ => $"{bytes / 1_073_741_824.0:0.##} GB",
    };

    [SubSlashCommand("backfill", "Archive the history from before the bot was logging")]
    public async Task<InteractionMessageProperties> BackfillAsync(
        [SlashCommandParameter(Description = "start or resume; pause; restart from the newest messages (fetches fresh attachment links)")] BackfillAction action)
    {
        if (!await modules.IsEnabledAsync(GuildId, Archiver.ModuleId))
            return Replies.Ephemeral($"The `{Archiver.ModuleId}` module is off.");

        if (action == BackfillAction.Pause)
            await backfiller.PauseAsync(GuildId);
        else
            await backfiller.StartAsync(GuildId, restart: action == BackfillAction.Restart);
        return await StatusAsync();
    }

    [SubSlashCommand("settings", "Attachments and excluded channels")]
    public async Task<InteractionMessageProperties> SettingsAsync(
        [SlashCommandParameter(Name = "save-attachments", Description = "Download attachment files (when storage is configured)")] bool? saveAttachments = null,
        [SlashCommandParameter(Name = "max-attachment-mb", Description = "Larger files are recorded but not downloaded; 0 means no limit", MinValue = 0, MaxValue = 100_000)] int? maxMb = null,
        [SlashCommandParameter(Description = "A channel to stop archiving")] Channel? exclude = null,
        [SlashCommandParameter(Description = "An excluded channel to archive again")] Channel? include = null)
    {
        var before = await settings.GetAsync<ArchiveRules>(GuildId, Archiver.ModuleId);
        var excluded = before.ExcludedChannelIds.Where(id => id != include?.Id).ToList();
        if (exclude is not null && !excluded.Contains(exclude.Id))
            excluded.Add(exclude.Id);

        var after = before with
        {
            SaveAttachments = saveAttachments ?? before.SaveAttachments,
            MaxAttachmentBytes = maxMb is null ? before.MaxAttachmentBytes : maxMb == 0 ? null : maxMb * 1_048_576L,
            ExcludedChannelIds = excluded,
        };

        if (!SettingsStore.Same(before, after))
            await settings.SetAsync(GuildId, Archiver.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return await StatusAsync();
    }
}

public enum BackfillAction
{
    Start,
    Pause,
    Restart,
}
