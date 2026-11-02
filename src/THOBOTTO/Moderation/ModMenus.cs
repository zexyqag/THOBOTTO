using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// Right-click a member, Apps: Warn, Time out, Lift timeout, Kick or Ban, each asking why (and how long), with /mod's
// checks.
public sealed class ModMenus(ModuleState modules, TimeProvider time) : ModModule(modules)
{
    [UserCommand("Warn", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
    [RequirePermission(BotPermissions.ModWarn)]
    public Task<InteractionCallbackProperties> WarnAsync(User user) => FormAsync(user, ModForms.Warn(user.Id));

    [UserCommand("Time out", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
    [RequirePermission(BotPermissions.ModTimeout)]
    public Task<InteractionCallbackProperties> TimeoutAsync(User user) => FormAsync(user, new ModalProperties($"modmenutimeout:{user.Id}", "Time out")
    {
        new LabelProperties("How long? (at most 28 days)", new TextInputProperties("duration", TextInputStyle.Short) { Value = "1h", Placeholder = "10m, 1h, 2d, 1w", MaxLength = 20 }),
        ModForms.Why(required: true),
    });

    [UserCommand("Lift timeout", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
    [RequirePermission(BotPermissions.ModTimeout)]
    public Task<InteractionCallbackProperties> UntimeoutAsync(User user)
        => (user as GuildUser)?.TimeOutUntil is not { } until || until <= time.GetUtcNow()
            ? Task.FromResult<InteractionCallbackProperties>(InteractionCallback.Message(Replies.Ephemeral($"<@{user.Id}> isn't timed out.")))
            : FormAsync(user, new ModalProperties($"modmenuuntimeout:{user.Id}", "Lift timeout") { ModForms.Why(required: false) });

    [UserCommand("Kick", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.KickUsers)]
    [RequirePermission(BotPermissions.ModKick)]
    public Task<InteractionCallbackProperties> KickAsync(User user)
        => user is GuildUser
            ? FormAsync(user, new ModalProperties($"modmenukick:{user.Id}", "Kick") { ModForms.Why(required: true) })
            : Task.FromResult<InteractionCallbackProperties>(InteractionCallback.Message(Replies.Ephemeral($"<@{user.Id}> isn't in the server.")));

    [UserCommand("Ban", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.BanUsers)]
    [RequirePermission(BotPermissions.ModBan)]
    public Task<InteractionCallbackProperties> BanAsync(User user) => FormAsync(user, new ModalProperties($"modmenuban:{user.Id}", "Ban")
    {
        ModForms.Why(required: true),
        new LabelProperties("How long? (leave empty: for good)", new TextInputProperties("duration", TextInputStyle.Short) { Required = false, Placeholder = "12h, 1d, 2w", MaxLength = 20 }),
        new LabelProperties("Also delete their recent messages?", new StringMenuProperties("delete",
        [
            new("No", "0") { Default = true },
            new("From the last hour", "3600"),
            new("From the last day", "86400"),
            new("From the last week", "604800"),
        ])),
    });

    private async Task<InteractionCallbackProperties> FormAsync(User user, ModalProperties form)
        => await RefusalAsync(user as GuildUser, user.Id) is { } refusal
            ? InteractionCallback.Message(Replies.Ephemeral(refusal))
            : InteractionCallback.Modal(form);
}

public static class ModForms
{
    public static LabelProperties Why(bool required)
        => new(required ? "Why? The member sees this." : "Why? (optional; the member sees this)", new TextInputProperties("reason", TextInputStyle.Short) { Required = required, MaxLength = 500 });

    public static ModalProperties Warn(ulong userId) => new($"modmenuwarn:{userId}", "Warn") { Why(required: true) };
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

    [ComponentInteraction("modmenuuntimeout")]
    public async Task UntimeoutAsync(ulong userId)
    {
        var reason = Inputs().GetValueOrDefault("reason");
        await RunAsync(BotPermissions.ModTimeout, userId, () => actions.UntimeoutAsync(Me, userId, string.IsNullOrEmpty(reason) ? null : reason));
    }

    [ComponentInteraction("modmenukick")]
    public async Task KickAsync(ulong userId)
    {
        var inputs = Inputs();
        await RunAsync(BotPermissions.ModKick, userId, () => actions.KickAsync(Me, userId, inputs["reason"]));
    }

    [ComponentInteraction("modmenuban")]
    public async Task BanAsync(ulong userId)
    {
        var inputs = Inputs();
        var duration = inputs.GetValueOrDefault("duration");
        var length = string.IsNullOrEmpty(duration) ? null : Durations.Parse(duration);
        if (!string.IsNullOrEmpty(duration) && length is null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Durations look like 1d, 2w or 12h.")));
            return;
        }
        var seconds = Context.Components.OfType<Label>().Select(l => l.Component).OfType<StringMenu>().FirstOrDefault()?.SelectedValues is [var picked, ..] && int.TryParse(picked, out var s) ? s : 0;
        await RunAsync(BotPermissions.ModBan, userId, async () => await actions.BanAsync(Me, userId, inputs["reason"], length, seconds, member: await MemberAsync(userId) is not null));
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
