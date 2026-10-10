using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Modules;

namespace THOBOTTO.Gate;

// Needs the Server Members Intent: without it, Discord doesn't say who joins.
public sealed class GateJoinHandler(Gatekeeper gate, ILogger<GateJoinHandler> logger) : IGuildUserAddGatewayHandler
{
    public async ValueTask HandleAsync(GuildUser user)
    {
        try
        {
            await gate.JoinedAsync(user);
        }
        catch (Exception ex) when (ex is RestException or InvalidOperationException)
        {
            logger.LogWarning("The gate couldn't handle {UserId} joining {GuildId}: {Message}", user.Id, user.GuildId, ex.Message);
        }
    }
}

public sealed class GateButtons(Gatekeeper gate, AccessControl access) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction(Gatekeeper.VerifyButton)]
    public async Task<InteractionCallbackProperties> VerifyAsync()
    {
        var user = (GuildUser)Context.User;
        if (await gate.VerifyAsync(user) is { } reply)
            return InteractionCallback.Message(Replies.Ephemeral(reply));
        var rules = await gate.RulesAsync(user.GuildId);
        return InteractionCallback.Modal(new ModalProperties("gateanswer", "One question")
        {
            new LabelProperties(rules.Question!.Length <= 45 ? rules.Question : "Your answer", new TextInputProperties("answer", TextInputStyle.Short) { MaxLength = 200 })
            {
                Description = rules.Question.Length <= 45 ? null : rules.Question[..Math.Min(100, rules.Question.Length)],
            },
        });
    }

    [ComponentInteraction("gatelet")]
    public Task<InteractionMessageProperties> LetInAsync(ulong userId) => DecideAsync(moderator => gate.LetInAsync(moderator.GuildId, userId, moderator));

    [ComponentInteraction("gatekick")]
    public Task<InteractionMessageProperties> KickAsync(ulong userId) => DecideAsync(moderator => gate.KickAsync(moderator.GuildId, userId, moderator));

    // Whoever may kick decides who comes in.
    private async Task<InteractionMessageProperties> DecideAsync(Func<GuildUser, Task<string>> decide)
    {
        var moderator = (GuildUser)Context.User;
        if (!await access.CanAsync(Context.Guild!, moderator, BotPermissions.ModKick))
            return Replies.Ephemeral($"Deciding who comes in needs `{BotPermissions.ModKick}`.");
        return Replies.Ephemeral(await decide(moderator));
    }
}

public sealed class GateAnswers(Gatekeeper gate) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("gateanswer")]
    public async Task<InteractionMessageProperties> AnswerAsync()
    {
        var answer = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        return Replies.Ephemeral(await gate.AnswerAsync((GuildUser)Context.User, answer));
    }
}
