using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Discord;

namespace THOBOTTO.Fame;

public sealed class FameReactionAddHandler(HallOfFame fame, AsMember asMember) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        if (arg.GuildId is { } guildId && arg.User?.IsBot != true && await asMember.AuthorOfAsync(guildId, arg.MessageId, arg.MessageAuthorId) is { } authorId)
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
