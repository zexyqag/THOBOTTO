using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Points;

public sealed class PointsMessageHandler(PointsEngine points) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg)
    {
        if (arg.GuildId is { } guildId && !arg.Author.IsBot && arg.WebhookId is null)
            await points.OnMessageAsync(guildId, arg.Author.Id, arg.Content.Length);
    }
}

public sealed class PointsReactionHandler(PointsEngine points) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        if (arg.GuildId is { } guildId && arg.MessageAuthorId is { } authorId && arg.User?.IsBot != true)
            await points.OnReactionAsync(guildId, arg.UserId, authorId, arg.MessageId);
    }
}
