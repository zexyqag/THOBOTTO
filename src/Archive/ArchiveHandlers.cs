using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Archive;

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
