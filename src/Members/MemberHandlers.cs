using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Members;

// Needs the Server Members Intent: without it, Discord doesn't say who leaves.
public sealed class MemberLeftHandler(MemberLeaving leaving, ILogger<MemberLeftHandler> logger) : IGuildUserRemoveGatewayHandler
{
    public async ValueTask HandleAsync(GuildUserRemoveEventArgs arg)
    {
        if (arg.User.IsBot)
            return;
        logger.LogInformation("{UserId} left {GuildId}; their preferences there are forgotten", arg.User.Id, arg.GuildId);
        await leaving.LeftAsync(arg.GuildId, arg.User.Id);
    }
}

// The bot was removed from a server (an outage looks the same, but says so): said, and nothing deleted, since what
// is kept is the server's history and the bot may be back.
public sealed class GuildLeftHandler(ILogger<GuildLeftHandler> logger) : IGuildDeleteGatewayHandler
{
    public ValueTask HandleAsync(GuildDeleteEventArgs arg)
    {
        if (!arg.IsUnavailable)
            logger.LogInformation("The bot was removed from {GuildId}; everything kept about the server stays", arg.GuildId);
        return default;
    }
}
