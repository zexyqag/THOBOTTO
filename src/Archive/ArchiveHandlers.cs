using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

public sealed class ArchiveGuildCreateHandler(DeletionWitness witness, ModuleState modules, ILogger<ArchiveGuildCreateHandler> logger) : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        _ = PrimeAsync(arg.GuildId);
        return default;
    }

    private async Task PrimeAsync(ulong guildId)
    {
        try
        {
            if (await modules.IsEnabledAsync(guildId, Archiver.ModuleId))
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
