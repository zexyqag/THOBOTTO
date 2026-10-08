using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Fame;

public sealed class FameReactionAddHandler(HallOfFame fame) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        if (arg.GuildId is { } guildId && arg.MessageAuthorId is { } authorId && arg.User?.IsBot != true)
            await fame.OnReactionAddedAsync(guildId, arg.ChannelId, arg.MessageId, authorId, arg.UserId, HallOfFame.EmojiKey(arg.Emoji));
    }
}

public sealed class FameReactionRemoveHandler(HallOfFame fame) : IMessageReactionRemoveGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionRemoveEventArgs arg)
    {
        if (arg.GuildId is not null)
            await fame.OnReactionRemovedAsync(arg.MessageId, arg.UserId, HallOfFame.EmojiKey(arg.Emoji));
    }
}
