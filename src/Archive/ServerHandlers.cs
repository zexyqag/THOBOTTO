using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Archive;

// The server's structure and people as they change, through the Archiver's queue; what Discord only
// tells in full (emojis, stickers, bans, webhooks, events, pins, the guild itself) is taken again by the
// snapshotter. Member events need the Server Members intent and don't arrive without it.
public sealed class ArchiveChannelHandler(Archiver archiver) : IGuildChannelCreateGatewayHandler, IGuildChannelUpdateGatewayHandler, IGuildChannelDeleteGatewayHandler
{
    ValueTask IGuildChannelCreateGatewayHandler.HandleAsync(IGuildChannel arg)
    {
        archiver.OnChannelSeen(arg.GuildId, arg);
        return default;
    }

    ValueTask IGuildChannelUpdateGatewayHandler.HandleAsync(IGuildChannel arg)
    {
        archiver.OnChannelSeen(arg.GuildId, arg);
        return default;
    }

    ValueTask IGuildChannelDeleteGatewayHandler.HandleAsync(IGuildChannel arg)
    {
        archiver.OnChannelGone(arg.GuildId, arg.Id, arg);
        return default;
    }
}

public sealed class ArchiveThreadHandler(Archiver archiver) : IGuildThreadCreateGatewayHandler, IGuildThreadUpdateGatewayHandler, IGuildThreadDeleteGatewayHandler, IGuildThreadListSyncGatewayHandler
{
    public ValueTask HandleAsync(GuildThreadCreateEventArgs arg)
    {
        archiver.OnChannelSeen(arg.Thread.GuildId, arg.Thread);
        return default;
    }

    public ValueTask HandleAsync(GuildThread arg)
    {
        archiver.OnChannelSeen(arg.GuildId, arg);
        return default;
    }

    public ValueTask HandleAsync(GuildThreadDeleteEventArgs arg)
    {
        archiver.OnChannelGone(arg.GuildId, arg.ThreadId, null);
        return default;
    }

    public ValueTask HandleAsync(GuildThreadListSyncEventArgs arg)
    {
        foreach (var thread in arg.Threads.Values)
            archiver.OnChannelSeen(arg.GuildId, thread);
        return default;
    }
}

public sealed class ArchiveRoleHandler(Archiver archiver) : IRoleCreateGatewayHandler, IRoleUpdateGatewayHandler, IRoleDeleteGatewayHandler
{
    ValueTask IRoleCreateGatewayHandler.HandleAsync(Role arg)
    {
        archiver.OnRoleSeen(arg.GuildId, arg);
        return default;
    }

    ValueTask IRoleUpdateGatewayHandler.HandleAsync(Role arg)
    {
        archiver.OnRoleSeen(arg.GuildId, arg);
        return default;
    }

    public ValueTask HandleAsync(RoleDeleteEventArgs arg)
    {
        archiver.OnRoleGone(arg.GuildId, arg.RoleId);
        return default;
    }
}

public sealed class ArchiveMemberHandler(Archiver archiver) : IGuildUserAddGatewayHandler, IGuildUserUpdateGatewayHandler, IGuildUserRemoveGatewayHandler
{
    ValueTask IGuildUserAddGatewayHandler.HandleAsync(GuildUser arg)
    {
        archiver.OnMemberSeen(arg.GuildId, arg);
        return default;
    }

    ValueTask IGuildUserUpdateGatewayHandler.HandleAsync(GuildUser arg)
    {
        archiver.OnMemberSeen(arg.GuildId, arg);
        return default;
    }

    public ValueTask HandleAsync(GuildUserRemoveEventArgs arg)
    {
        archiver.OnMemberLeft(arg.GuildId, arg.User);
        return default;
    }
}

public sealed class ArchiveGuildChangeHandler(GuildSnapshotter snapshotter)
    : IGuildUpdateGatewayHandler, IGuildEmojisUpdateGatewayHandler, IGuildStickersUpdateGatewayHandler, IGuildBanAddGatewayHandler, IGuildBanRemoveGatewayHandler,
      IWebhooksUpdateGatewayHandler, IGuildScheduledEventCreateGatewayHandler, IGuildScheduledEventUpdateGatewayHandler, IGuildScheduledEventDeleteGatewayHandler,
      IChannelPinsUpdateGatewayHandler
{
    public ValueTask HandleAsync(Guild arg)
    {
        snapshotter.Request(arg.Id, SnapshotPart.Guild);
        return default;
    }

    public ValueTask HandleAsync(GuildEmojisUpdateEventArgs arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Emojis);
        return default;
    }

    public ValueTask HandleAsync(GuildStickersUpdateEventArgs arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Stickers);
        return default;
    }

    ValueTask IGuildBanAddGatewayHandler.HandleAsync(GuildBanEventArgs arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Bans);
        return default;
    }

    ValueTask IGuildBanRemoveGatewayHandler.HandleAsync(GuildBanEventArgs arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Bans);
        return default;
    }

    public ValueTask HandleAsync(WebhooksUpdateEventArgs arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Webhooks);
        return default;
    }

    ValueTask IGuildScheduledEventCreateGatewayHandler.HandleAsync(GuildScheduledEvent arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Events);
        return default;
    }

    ValueTask IGuildScheduledEventUpdateGatewayHandler.HandleAsync(GuildScheduledEvent arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Events);
        return default;
    }

    ValueTask IGuildScheduledEventDeleteGatewayHandler.HandleAsync(GuildScheduledEvent arg)
    {
        snapshotter.Request(arg.GuildId, SnapshotPart.Events);
        return default;
    }

    public ValueTask HandleAsync(ChannelPinsUpdateEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            snapshotter.RequestPins(guildId, arg.ChannelId);
        return default;
    }
}
