using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

// A server seen (at a start, or on a reconnect): the server is snapshotted unless it just was, and the
// deletion witness primed.
public sealed class ArchiveGuildCreateHandler(DeletionWitness witness, GuildSnapshotter snapshotter, ModuleState modules, ILogger<ArchiveGuildCreateHandler> logger)
    : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        if (arg.Guild is { IsUnavailable: false })
            _ = StartAsync(arg.GuildId);
        return default;
    }

    private async Task StartAsync(ulong guildId)
    {
        try
        {
            if (!await modules.IsEnabledAsync(guildId, Archiver.ModuleId))
                return;
            snapshotter.RequestWhole(guildId, force: false);
            await witness.PrimeAsync(guildId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the deletion audit log of {GuildId} failed", guildId);
        }
    }
}

public sealed class ArchiveCreateHandler(Archiver archiver) : IMessageCreateGatewayHandler
{
    public ValueTask HandleAsync(Message arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnCreated(guildId, arg);
        return default;
    }
}

public sealed class ArchiveUpdateHandler(Archiver archiver) : IMessageUpdateGatewayHandler
{
    public ValueTask HandleAsync(Message arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnUpdated(guildId, arg);
        return default;
    }
}

public sealed class ArchiveDeleteHandler(Archiver archiver) : IMessageDeleteGatewayHandler
{
    public ValueTask HandleAsync(MessageDeleteEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnDeleted(guildId, arg.ChannelId, [arg.MessageId]);
        return default;
    }
}

public sealed class ArchiveDeleteBulkHandler(Archiver archiver) : IMessageDeleteBulkGatewayHandler
{
    public ValueTask HandleAsync(MessageDeleteBulkEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnDeleted(guildId, arg.ChannelId, arg.MessageIds);
        return default;
    }
}

public sealed class ArchiveReactionAddHandler(Archiver archiver) : IMessageReactionAddGatewayHandler
{
    public ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnReactionAdded(guildId, arg.ChannelId, arg.MessageId, new(arg.UserId, arg.Emoji.Id, arg.Emoji.Name ?? "", arg.Burst));
        return default;
    }
}

public sealed class ArchiveReactionRemoveHandler(Archiver archiver) : IMessageReactionRemoveGatewayHandler
{
    public ValueTask HandleAsync(MessageReactionRemoveEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnReactionRemoved(guildId, arg.ChannelId, arg.MessageId, new(arg.UserId, arg.Emoji.Id, arg.Emoji.Name ?? "", arg.Burst));
        return default;
    }
}

public sealed class ArchiveReactionRemoveAllHandler(Archiver archiver) : IMessageReactionRemoveAllGatewayHandler
{
    public ValueTask HandleAsync(MessageReactionRemoveAllEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnReactionsRemovedAll(guildId, arg.ChannelId, arg.MessageId);
        return default;
    }
}

public sealed class ArchiveReactionRemoveEmojiHandler(Archiver archiver) : IMessageReactionRemoveEmojiGatewayHandler
{
    public ValueTask HandleAsync(MessageReactionRemoveEmojiEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnReactionsRemovedEmoji(guildId, arg.ChannelId, arg.MessageId, arg.Emoji.Id, arg.Emoji.Name ?? "");
        return default;
    }
}

public sealed class ArchivePollVoteAddHandler(Archiver archiver) : IMessagePollVoteAddGatewayHandler
{
    public ValueTask HandleAsync(MessagePollVoteEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnVoteAdded(guildId, arg.ChannelId, arg.MessageId, new(arg.UserId, arg.AnswerId));
        return default;
    }
}

public sealed class ArchivePollVoteRemoveHandler(Archiver archiver) : IMessagePollVoteRemoveGatewayHandler
{
    public ValueTask HandleAsync(MessagePollVoteEventArgs arg)
    {
        if (arg.GuildId is { } guildId)
            archiver.OnVoteRemoved(guildId, arg.ChannelId, arg.MessageId, new(arg.UserId, arg.AnswerId));
        return default;
    }
}
