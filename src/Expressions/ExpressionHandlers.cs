using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Expressions;

public sealed class ExpressionMessageHandler(ExpressionShelf shelf) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg)
    {
        if (arg.GuildId is { } guildId && !arg.Author.IsBot && arg.WebhookId is null)
            await shelf.OnMessageAsync(guildId, arg.Author.Id, arg.Content, arg.Stickers.Select(s => s.Id));
    }
}

public sealed class ExpressionReactionHandler(ExpressionShelf shelf) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        if (arg.GuildId is { } guildId && arg.Emoji.Id is { } emojiId && arg.User?.IsBot != true)
            await shelf.OnReactionAsync(guildId, arg.UserId, emojiId);
    }
}
