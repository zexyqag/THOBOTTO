using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;

namespace THOBOTTO.Reposts;

// Right-click a post the bot made under your name, Apps, Edit my post: its text changed (its words rewritten again),
// or emptied to remove it.
public sealed class EditMyPostCommand(AsMember asMember) : GuildCommandModule
{
    [MessageCommand("Edit my post", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> EditAsync(RestMessage message)
    {
        if (message.WebhookId is null || await asMember.FindAsync(message.Id) is not { } post || post.UserId != Context.User.Id)
            return InteractionCallback.Message(Replies.Ephemeral("Only a post the bot made under your name can be edited here."));
        return InteractionCallback.Modal(new ModalProperties($"editmypost:{message.Id}", "Edit your post")
        {
            new LabelProperties("Its text (empty removes it)", new TextInputProperties("text", TextInputStyle.Paragraph) { Value = message.Content, MaxLength = 2000, Required = false }),
        });
    }
}

public sealed class EditMyPostModal(AsMember asMember, Reposter reposter) : GuildModalModule
{
    [ComponentInteraction("editmypost")]
    public async Task<InteractionCallbackProperties> EditAsync(ulong messageId)
    {
        if (await asMember.FindAsync(messageId) is not { } post || post.UserId != Context.User.Id)
            return InteractionCallback.Message(Replies.Ephemeral("That post is gone."));
        var text = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value.Trim();
        if (text.Length == 0)
            return InteractionCallback.Message(Replies.Ephemeral(await asMember.DeleteAsync(post) is { } unremoved ? $"It couldn't be removed ({unremoved})." : "Removed."));
        if (Context.Channel is TextGuildChannel channel)
            text = await reposter.RewriteAsync(Guild, (GuildUser)Context.User, channel, text);
        if (text.Length > 2000)
            return InteractionCallback.Message(Replies.Ephemeral("Rewritten, it's longer than Discord's 2,000 characters."));
        return InteractionCallback.Message(Replies.Ephemeral(await asMember.EditAsync(post, text) is { } problem ? $"It couldn't be edited ({problem})." : "Edited."));
    }
}
