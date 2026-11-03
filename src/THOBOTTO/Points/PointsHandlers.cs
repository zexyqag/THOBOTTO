using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Discord;

namespace THOBOTTO.Points;

public sealed class PointsMessageHandler(PointsEngine points) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg)
    {
        if (arg.GuildId is { } guildId && !arg.Author.IsBot && arg.WebhookId is null)
            await points.OnMessageAsync(guildId, arg.Author.Id, arg.Content.Length);
    }
}

public sealed class PointsReactionHandler(PointsEngine points, AsMember asMember) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        if (arg.GuildId is { } guildId && arg.User?.IsBot != true && await asMember.AuthorOfAsync(guildId, arg.MessageId, arg.MessageAuthorId) is { } authorId)
            await points.OnReactionAsync(guildId, arg.UserId, authorId, arg.MessageId);
    }
}
