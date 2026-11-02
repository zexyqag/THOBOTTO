using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// Right-click a member, Apps: Warn or Time out, each asking why (and how long), with /mod's checks.
public sealed class ModMenus(ModuleState modules) : ModModule(modules)
{
    [UserCommand("Warn", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
    [RequirePermission(BotPermissions.ModWarn)]
    public async Task<InteractionCallbackProperties> WarnAsync(User user)
        => await RefusalAsync(user as GuildUser, user.Id) is { } refusal
            ? InteractionCallback.Message(Replies.Ephemeral(refusal))
            : InteractionCallback.Modal(new ModalProperties($"modmenuwarn:{user.Id}", "Warn")
            {
                new LabelProperties("Why? The member sees this.", new TextInputProperties("reason", TextInputStyle.Short) { MaxLength = 500 }),
            });

    [UserCommand("Time out", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
    [RequirePermission(BotPermissions.ModTimeout)]
    public async Task<InteractionCallbackProperties> TimeoutAsync(User user)
        => await RefusalAsync(user as GuildUser, user.Id) is { } refusal
            ? InteractionCallback.Message(Replies.Ephemeral(refusal))
            : InteractionCallback.Modal(new ModalProperties($"modmenutimeout:{user.Id}", "Time out")
            {
                new LabelProperties("How long? (at most 28 days)", new TextInputProperties("duration", TextInputStyle.Short) { Value = "1h", Placeholder = "10m, 1h, 2d, 1w", MaxLength = 20 }),
                new LabelProperties("Why? The member sees this.", new TextInputProperties("reason", TextInputStyle.Short) { MaxLength = 500 }),
            });
}

// A form carries no permission of its own: the checks are made again.
public sealed class ModMenuModals(ModActions actions, AccessControl access, RestClient rest) : GuildModalModule
{
    [ComponentInteraction("modmenuwarn")]
    public async Task WarnAsync(ulong userId)
    {
        var inputs = Inputs();
        await RunAsync(BotPermissions.ModWarn, userId, () => actions.WarnAsync(Me, userId, inputs["reason"]));
    }

    [ComponentInteraction("modmenutimeout")]
    public async Task TimeoutAsync(ulong userId)
    {
        var inputs = Inputs();
        var length = Durations.Parse(inputs["duration"]);
        if (length is null || length > ModActions.MaxTimeout)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(length is null ? "Durations look like 10m, 1h30m, 2d or 1w." : "Discord allows timeouts of up to 28 days.")));
            return;
        }
        await RunAsync(BotPermissions.ModTimeout, userId, () => actions.TimeoutAsync(Me, userId, length.Value, inputs["reason"]));
    }

    private Actor Me => new(GuildId, Context.User.Id, Context.User.Username);

    private async Task<GuildUser?> MemberAsync(ulong userId)
    {
        try
        {
            return await rest.GetGuildUserAsync(GuildId, userId);
        }
        catch (RestException)
        {
            return null;
        }
    }

    private Dictionary<string, string> Inputs()
        => Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());

    private async Task RunAsync(string permission, ulong userId, Func<Task<string>> action)
    {
        var actor = (GuildUser)Context.User;
        var member = Guild.Users.TryGetValue(userId, out var cached) ? cached : await MemberAsync(userId);
        var refusal = !await access.CanAsync(Guild, actor, permission) ? $"That needs `{permission}`."
            : member is not null && !AccessControl.Outranks(Guild, actor, member, allowEqual: false) ? $"<@{userId}> doesn't rank below you."
            : null;
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await action();
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (result, AllowedMentionsProperties.None));
    }
}
