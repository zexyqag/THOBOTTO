using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Archive;

// What the archive holds of the server itself and of the reactions, and the last export, for `/archive status`
// and the Archive page.
public sealed record ServerStatus(
    DateTimeOffset? TakenAt,
    IReadOnlyList<(string Kind, string Problem)> Problems,
    int Channels,
    int DeletedChannels,
    int Roles,
    int Members,
    int Left,
    int Reactions,
    int CurrentReactions,
    int Votes,
    int Images,
    ExportRecord? LastExport,
    string? ExportProblem)
{
    public static async Task<ServerStatus> LoadAsync(BotDbContext db, ulong guildId)
    {
        var messages = db.ArchivedMessages.Where(m => m.GuildId == guildId);
        var reactions = db.ArchivedReactions.Where(r => messages.Any(m => m.Id == r.MessageId));
        var channels = db.ArchivedChannels.Where(c => c.GuildId == guildId);
        var members = db.ArchivedMembers.Where(m => m.GuildId == guildId);
        var snapshots = await db.GuildSnapshots.Where(s => s.GuildId == guildId).ToListAsync();
        var export = snapshots.FirstOrDefault(s => s.Kind == SnapshotKinds.Export);
        return new(
            snapshots.FirstOrDefault(s => s.Kind == SnapshotKinds.Channels)?.TakenAt,
            snapshots.Where(s => s.Problem is not null && s.Kind != SnapshotKinds.Export).Select(s => (s.Kind, s.Problem!)).ToList(),
            await channels.CountAsync(),
            await channels.CountAsync(c => c.DeletedAt != null),
            await db.ArchivedRoles.CountAsync(r => r.GuildId == guildId && r.DeletedAt == null),
            await members.CountAsync(),
            await members.CountAsync(m => m.LeftAt != null),
            await reactions.CountAsync(),
            await reactions.CountAsync(r => r.RemovedAt == null),
            await db.ArchivedPollVotes.CountAsync(v => messages.Any(m => m.Id == v.MessageId)),
            await db.ArchivedAssets.CountAsync(a => a.GuildId == guildId && a.StoredKey != null),
            ExportRecord.Parse(export),
            export?.Problem);
    }

    // "taken 2 days ago to /export: 1,234 files, 56,789 messages", with what the panel or Discord makes of the time.
    public string? Exported(Func<DateTimeOffset, string> when)
        => LastExport is { } last ? $"taken {when(last.At)} to {last.Folder}: {last.Files:N0} files, {last.Messages:N0} messages" : null;

    public string Counts => $"{Channels} channels and threads ({DeletedChannels} deleted), {Roles} roles, {Members} members ({Left} left)";

    // "members: the Server Members intent is off in the Developer Portal; bans: no Ban Members permission".
    public string? CouldNotTake => Problems.Count == 0 ? null : string.Join("; ", Problems.Select(p => $"{p.Kind}: {p.Problem}"));
}
