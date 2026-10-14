using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.JsonModels;

using THOBOTTO.Data;

namespace THOBOTTO.Archive;

public sealed record SeenPin(ulong MessageId, DateTimeOffset PinnedAt);

// Writes the server's channels, roles, members and snapshots for the Archiver's writer. Rows are never
// removed: a complete listing marks what it no longer holds as deleted or left, and a single sighting
// brings a row back. A listing is applied after the calls it took, so it leaves alone what was seen or
// marked gone since it began.
public sealed class ServerLedger(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<ServerLedger> logger)
{
    internal async Task ApplyAsync(Work work, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        switch (work)
        {
            case ChannelsListed listed:
                await ListChannelsAsync(db, listed, now, ct);
                break;
            case ChannelSeen seen:
                await ChannelRowAsync(db, seen.GuildId, seen.Channel, now, ct);
                break;
            case ChannelGone gone:
                var channel = gone.Last is { } last ? await ChannelRowAsync(db, gone.GuildId, last, now, ct) : await db.ArchivedChannels.FindAsync([gone.Id], ct);
                if (channel is not null)
                    channel.DeletedAt = now;
                break;
            case RolesListed listed:
                await ListRolesAsync(db, listed, now, ct);
                break;
            case RoleSeen seen:
                await RoleRowAsync(db, seen.GuildId, seen.Role, now, ct);
                break;
            case RoleGone gone:
                if (await db.ArchivedRoles.FindAsync([gone.Id], ct) is { } role)
                    role.DeletedAt = now;
                break;
            case MembersListed listed:
                await ListMembersAsync(db, listed, now, ct);
                break;
            case MemberSeen seen:
                await MemberRowAsync(db, seen.GuildId, seen.Member, now, ct);
                break;
            case MemberLeft left:
                await LeaveAsync(db, left, now, ct);
                break;
            case SnapshotTaken taken:
                await SnapshotRowAsync(db, taken.GuildId, taken.Kind, taken.Json, taken.Problem, now, ct);
                break;
            case PinsSeen pins:
                await PinsAsync(db, pins, now, ct);
                break;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ListChannelsAsync(BotDbContext db, ChannelsListed listed, DateTimeOffset now, CancellationToken ct)
    {
        var known = await db.ArchivedChannels.Where(c => c.GuildId == listed.GuildId).ToDictionaryAsync(c => c.Id, ct);
        var diff = SnapshotDiff.Of(known.ToDictionary(k => k.Key, k => k.Value.DeletedAt is not null), listed.List.Channels.Select(c => c.Id).ToList(),
            id => !SnapshotDiff.Touched(known[id].LastSeenAt, known[id].DeletedAt, listed.StartedAt) && listed.List.Vouches(known[id].Type, known[id].ParentId));
        foreach (var channel in listed.List.Channels)
        {
            if (known.GetValueOrDefault(channel.Id) is not { } row)
                Fill(Add(db, listed.GuildId, channel, now), channel, now);
            else if (!SnapshotDiff.Touched(row.LastSeenAt, row.DeletedAt, listed.StartedAt))
                Fill(row, channel, now);
        }
        foreach (var id in diff.Gone)
            known[id].DeletedAt = now;
        logger.LogInformation("Channels and threads of {GuildId} listed: {Diff}", listed.GuildId, diff);
    }

    private async Task ListRolesAsync(BotDbContext db, RolesListed listed, DateTimeOffset now, CancellationToken ct)
    {
        var known = await db.ArchivedRoles.Where(r => r.GuildId == listed.GuildId).ToDictionaryAsync(r => r.Id, ct);
        var diff = SnapshotDiff.Of(known.ToDictionary(k => k.Key, k => k.Value.DeletedAt is not null), listed.Roles.Select(r => r.Id).ToList(),
            id => !SnapshotDiff.Touched(known[id].LastSeenAt, known[id].DeletedAt, listed.StartedAt));
        foreach (var role in listed.Roles)
        {
            if (known.GetValueOrDefault(role.Id) is not { } row)
                Fill(Add(db, listed.GuildId, role, now), role, now);
            else if (!SnapshotDiff.Touched(row.LastSeenAt, row.DeletedAt, listed.StartedAt))
                Fill(row, role, now);
        }
        foreach (var id in diff.Gone)
            known[id].DeletedAt = now;
        logger.LogInformation("Roles of {GuildId} listed: {Diff}", listed.GuildId, diff);
    }

    private async Task ListMembersAsync(BotDbContext db, MembersListed listed, DateTimeOffset now, CancellationToken ct)
    {
        var known = await db.ArchivedMembers.Where(m => m.GuildId == listed.GuildId).ToDictionaryAsync(m => m.UserId, ct);
        var diff = SnapshotDiff.Of(known.ToDictionary(k => k.Key, k => k.Value.LeftAt is not null), listed.Members.Select(m => m.Id).ToList(),
            id => !SnapshotDiff.Touched(known[id].LastSeenAt, known[id].LeftAt, listed.StartedAt));
        foreach (var member in listed.Members)
        {
            if (known.GetValueOrDefault(member.Id) is not { } row)
                Fill(Add(db, listed.GuildId, member, now), member, now);
            else if (!SnapshotDiff.Touched(row.LastSeenAt, row.LeftAt, listed.StartedAt))
                Fill(row, member, now);
        }
        foreach (var id in diff.Gone)
            known[id].LeftAt = now;
        logger.LogInformation("Members of {GuildId} listed: {Diff}", listed.GuildId, diff);
    }

    private static async Task<ArchivedChannel> ChannelRowAsync(BotDbContext db, ulong guildId, IGuildChannel channel, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.ArchivedChannels.FindAsync([channel.Id], ct) ?? Add(db, guildId, channel, now);
        Fill(row, channel, now);
        return row;
    }

    private static async Task RoleRowAsync(BotDbContext db, ulong guildId, Role role, DateTimeOffset now, CancellationToken ct)
        => Fill(await db.ArchivedRoles.FindAsync([role.Id], ct) ?? Add(db, guildId, role, now), role, now);

    private static async Task MemberRowAsync(BotDbContext db, ulong guildId, GuildUser member, DateTimeOffset now, CancellationToken ct)
        => Fill(await db.ArchivedMembers.FindAsync([guildId, member.Id], ct) ?? Add(db, guildId, member, now), member, now);

    private static async Task LeaveAsync(BotDbContext db, MemberLeft left, DateTimeOffset now, CancellationToken ct)
    {
        var user = ((IJsonModel<JsonUser>)left.User).JsonModel;
        var row = await db.ArchivedMembers.FindAsync([left.GuildId, left.User.Id], ct);
        if (row is null)
        {
            // Never seen as a member: what the leave says about them is all there is.
            row = new() { GuildId = left.GuildId, UserId = left.User.Id, Username = user.Username, RoleIds = "[]", Raw = RawJson.OfModel(new JsonGuildUser { User = user }), FirstSeenAt = now };
            db.ArchivedMembers.Add(row);
        }
        row.Username = user.Username;
        row.GlobalName = user.GlobalName;
        row.AvatarHash = user.AvatarHash;
        row.Bot = user.IsBot;
        row.LastSeenAt = now;
        row.LeftAt = now;
    }

    private static async Task SnapshotRowAsync(BotDbContext db, ulong guildId, string kind, string? json, string? problem, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.GuildSnapshots.FindAsync([guildId, kind], ct);
        if (row is null)
            db.GuildSnapshots.Add(row = new() { GuildId = guildId, Kind = kind });
        row.TriedAt = now;
        row.Problem = problem;
        if (problem is not null)
            return;
        row.TakenAt = now;
        row.Json = json;
    }

    // The pins kind: an object of channel id to its pins. A complete listing replaces it; one channel's
    // pins replace that channel's entry.
    private static async Task PinsAsync(BotDbContext db, PinsSeen pins, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.GuildSnapshots.FindAsync([pins.GuildId, SnapshotKinds.Pins], ct);
        if (row is null)
            db.GuildSnapshots.Add(row = new() { GuildId = pins.GuildId, Kind = SnapshotKinds.Pins });
        var json = !pins.Complete && row.Json is not null && JsonNode.Parse(row.Json) is JsonObject kept ? kept : [];
        foreach (var (channelId, list) in pins.Pins)
        {
            var entries = new JsonArray();
            foreach (var pin in list)
                entries.Add(new JsonObject { ["message_id"] = pin.MessageId.ToString(), ["pinned_at"] = pin.PinnedAt });
            json[channelId.ToString()] = entries;
        }
        row.Json = json.ToJsonString();
        row.TriedAt = now;
        row.Problem = null;
        if (pins.Complete)
            row.TakenAt = now;
    }

    private static ArchivedChannel Add(BotDbContext db, ulong guildId, IGuildChannel channel, DateTimeOffset now)
    {
        var row = new ArchivedChannel { Id = channel.Id, GuildId = guildId, Name = "", Raw = "{}", FirstSeenAt = now };
        db.ArchivedChannels.Add(row);
        return row;
    }

    private static ArchivedRole Add(BotDbContext db, ulong guildId, Role role, DateTimeOffset now)
    {
        var row = new ArchivedRole { Id = role.Id, GuildId = guildId, Name = "", Raw = "{}", FirstSeenAt = now };
        db.ArchivedRoles.Add(row);
        return row;
    }

    private static ArchivedMember Add(BotDbContext db, ulong guildId, GuildUser member, DateTimeOffset now)
    {
        var row = new ArchivedMember { GuildId = guildId, UserId = member.Id, Username = "", RoleIds = "[]", Raw = "{}", FirstSeenAt = now };
        db.ArchivedMembers.Add(row);
        return row;
    }

    private static void Fill(ArchivedChannel row, IGuildChannel channel, DateTimeOffset now)
    {
        var json = ((IJsonModel<JsonChannel>)channel).JsonModel;
        row.ParentId = json.ParentId;
        row.Type = json.Type;
        row.Name = json.Name ?? "";
        row.Topic = json.Topic;
        row.Position = json.Position;
        row.Nsfw = json.Nsfw == true;
        row.RateLimit = json.Slowmode;
        row.Flags = json.Flags ?? 0;
        row.AvailableTags = json.AvailableTags is null ? null : RawJson.OfModel(json.AvailableTags);
        row.AppliedTags = json.AppliedTags is null ? null : RawJson.OfModel(json.AppliedTags);
        row.OwnerId = json.OwnerId;
        // Threads made before Discord recorded a creation time are as old as their id.
        row.ThreadCreatedAt = json.Metadata is null ? null : json.Metadata.CreatedAt ?? Snowflake.Timestamp(json.Id);
        row.Archived = json.Metadata?.Archived == true;
        row.Locked = json.Metadata?.Locked == true;
        row.PermissionOverwrites = json.PermissionOverwrites is null ? null : RawJson.OfModel(json.PermissionOverwrites);
        row.Raw = RawJson.OfModel(json);
        row.LastSeenAt = now;
        row.DeletedAt = null;
    }

    private static void Fill(ArchivedRole row, Role role, DateTimeOffset now)
    {
        var json = ((IJsonModel<JsonRole>)role).JsonModel;
        row.Name = json.Name;
        row.Color = json.Colors?.PrimaryColor.RawValue ?? 0;
        row.Position = json.Position;
        row.Permissions = (ulong)json.Permissions;
        row.Hoist = json.Hoist;
        row.Mentionable = json.Mentionable;
        row.IconHash = json.IconHash;
        row.Managed = json.Managed;
        row.Raw = RawJson.OfModel(json);
        row.LastSeenAt = now;
        row.DeletedAt = null;
    }

    private static void Fill(ArchivedMember row, GuildUser member, DateTimeOffset now)
    {
        var json = ((IJsonModel<JsonGuildUser>)member).JsonModel;
        row.Username = json.User.Username;
        row.GlobalName = json.User.GlobalName;
        row.Nick = json.Nickname;
        row.AvatarHash = json.User.AvatarHash;
        row.GuildAvatarHash = json.GuildAvatarHash;
        row.RoleIds = RawJson.OfModel(json.RoleIds ?? []);
        row.JoinedAt = json.JoinedAt;
        row.PremiumSince = json.GuildBoostStart;
        row.Bot = json.User.IsBot;
        row.Raw = RawJson.OfModel(json);
        row.LastSeenAt = now;
        row.LeftAt = null;
    }
}
