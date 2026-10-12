using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;

namespace THOBOTTO.Feedback;

public sealed class FeedbackCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string Modal = "feedback";

    [SlashCommand("feedback", "Tell the bot's owner what works, what doesn't, or what you'd like")]
    public InteractionCallbackProperties Feedback() => InteractionCallback.Modal(new ModalProperties(Modal, "Feedback about THOBOTTO")
    {
        new LabelProperties("What's on your mind?", new TextInputProperties("text", TextInputStyle.Paragraph) { MaxLength = FeedbackBox.Longest, Placeholder = "Something broke, something's confusing, an idea…" }),
    });
}

public sealed class FeedbackModal(FeedbackBox box) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction(FeedbackCommands.Modal)]
    public async Task<InteractionCallbackProperties> SendAsync()
    {
        var text = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        var user = Context.User;
        var answer = await box.AddAsync(Context.Guild?.Id, Context.Guild?.Name, user.Id, (user as GuildUser)?.Nickname ?? user.GlobalName ?? user.Username, text, "discord");
        return InteractionCallback.Message(Replies.Ephemeral(answer));
    }
}
