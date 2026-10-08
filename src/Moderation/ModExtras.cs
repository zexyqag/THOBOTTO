using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;

namespace THOBOTTO.Moderation;

public static class History
{
    private const int Shown = 20;

    public static async Task<InteractionMessageProperties> ForAsync(CaseBook cases, ulong guildId, User member)
    {
        var all = await cases.HistoryAsync(guildId, member.Id);
        if (all.Count == 0)
            return Replies.Ephemeral($"<@{member.Id}> has a clean record.");

        var active = await cases.ActiveWarningsAsync(guildId, member.Id);
        var notes = all.Where(c => c.Type == CaseTypes.Note).ToList();
        var actions = all.Where(c => c.Type != CaseTypes.Note).ToList();
        var text = $"**{active}** active warning{(active == 1 ? "" : "s")} · {actions.Count} case{(actions.Count == 1 ? "" : "s")} · {notes.Count} note{(notes.Count == 1 ? "" : "s")}\n\n"
            + string.Join('\n', all.Take(Shown).Select(Describe.Line))
            + (all.Count > Shown ? $"\n…and {all.Count - Shown} older; `/mod case show` for any of them." : "");

        return new()
        {
            Embeds = [new() { Title = $"History of {member.Username}", Description = Describe.Short(text, 4000) }],
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}

public static class Pardon
{
    public static async Task<string> RunAsync(CaseBook cases, AccessControl access, Guild guild, GuildUser actor, int number, string? reason)
    {
        if (await cases.FindAsync(guild.Id, number) is not { } c)
            return "There's no such case.";
        if (c.Type != CaseTypes.Warn)
            return "Only warnings are pardoned; other actions are lifted.";
        if (c.PardonedAt is not null)
            return $"Case #{number} is already pardoned.";
        if (c.ModeratorId != actor.Id && !await access.CanAsync(guild, actor, BotPermissions.ModManage))
            return $"Only <@{c.ModeratorId}> or someone with `{BotPermissions.ModManage}` can pardon it.";

        await cases.ChangeAsync(guild.Id, number, actor.Id, "pardoned", x =>
        {
            x.PardonedAt = cases.Now;
            x.PardonedById = actor.Id;
            x.PardonReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        });
        return $"Case #{number} is pardoned.";
    }
}

public sealed class ModUserCommands(CaseBook cases, AccessControl access) : ApplicationCommandModule<ApplicationCommandContext>
{
    [UserCommand("Mod history", Contexts = [InteractionContextType.Guild])]
    [RequirePermission(BotPermissions.ModWarn)]
    public Task<InteractionMessageProperties> HistoryAsync(User user) => History.ForAsync(cases, Context.Guild!.Id, user);

    // Asks why, then deletes the message and warns its author with its text in the case.
    [MessageCommand("Delete and warn", Contexts = [InteractionContextType.Guild])]
    [RequirePermission(BotPermissions.ModMessages)]
    public async Task<InteractionCallbackProperties> DeleteAndWarnAsync(RestMessage message)
    {
        var guild = Context.Guild!;
        var actor = (GuildUser)Context.User;
        string? refusal = !await access.CanAsync(guild, actor, BotPermissions.ModWarn) ? $"That also needs `{BotPermissions.ModWarn}`."
            : message.Author.Id == actor.Id ? "Not on yourself."
            : message.Author is GuildUser author && !AccessControl.Outranks(guild, actor, author, allowEqual: false) ? $"<@{author.Id}> doesn't rank below you."
            : null;
        return refusal is not null
            ? InteractionCallback.Message(Replies.Ephemeral(refusal))
            : InteractionCallback.Modal(new ModalProperties($"moddelwarn:{message.ChannelId}:{message.Id}", "Delete and warn")
            {
                new LabelProperties("Why? The member sees this.", new TextInputProperties("reason", TextInputStyle.Short) { MaxLength = 500 }),
            });
    }
}

public sealed class ModDeleteWarnModal(ModActions actions, RestClient rest) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("moddelwarn")]
    public async Task DeleteAndWarnAsync(ulong channelId, ulong messageId)
    {
        var reason = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        string result;
        try
        {
            var message = await rest.GetMessageAsync(channelId, messageId);
            result = await actions.DeleteAndWarnAsync(new(Context.Guild!.Id, Context.User.Id, Context.User.Username), message, reason);
        }
        catch (RestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            result = "That message is already gone.";
        }
        await ModifyResponseAsync(m =>
        {
            m.Content = result;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }
}

// The Pardon button on a warning in the moderation log asks why first.
public sealed class ModLogButtons : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("modpardon")]
    public InteractionCallbackProperties Pardon(int number)
        => InteractionCallback.Modal(new ModalProperties($"modpardonwhy:{number}", $"Pardon case #{number}")
        {
            new LabelProperties("Why? (optional)", new TextInputProperties("reason", TextInputStyle.Short) { Required = false, MaxLength = 500 }),
        });
}

// Lift on a timeout or ban in the moderation log: asks why, then undoes it.
public sealed class ModLiftButtons : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("modlift")]
    public InteractionCallbackProperties Lift(int number)
        => InteractionCallback.Modal(new ModalProperties($"modliftwhy:{number}", $"Lift case #{number}")
        {
            new LabelProperties("Why? (optional)", new TextInputProperties("reason", TextInputStyle.Short) { Required = false, MaxLength = 500 }),
        });
}

public sealed class ModLiftModal(CaseBook cases, ModActions actions, AccessControl access) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("modliftwhy")]
    public async Task LiftAsync(int number)
    {
        var guild = Context.Guild!;
        var user = (GuildUser)Context.User;
        var reason = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        var c = await cases.FindAsync(guild.Id, number);
        var permission = c?.Type switch
        {
            CaseTypes.Ban => BotPermissions.ModBan,
            CaseTypes.Lock => BotPermissions.ModChannels,
            CaseTypes.Mute or CaseTypes.Deafen => BotPermissions.ModVoice,
            CaseTypes.RoleAdd or CaseTypes.RoleRemove => BotPermissions.ModRoles,
            _ => BotPermissions.ModTimeout,
        };
        string? refusal = c is null || CaseTypes.LiftedBy(c.Type) is null ? "That case can't be lifted."
            : c.EndedAt is not null ? $"Case #{number} is already over."
            : !await access.CanAsync(guild, user, permission) ? $"That needs `{permission}`."
            : null;
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }

        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var me = new Actor(guild.Id, user.Id, user.Username);
        var why = string.IsNullOrWhiteSpace(reason) ? $"lifted case #{number}" : reason;
        var result = await actions.LiftAsync(me, c!, why);
        await ModifyResponseAsync(m =>
        {
            m.Content = result;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }
}

public sealed class ModPardonModal(CaseBook cases, AccessControl access) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("modpardonwhy")]
    public async Task<InteractionMessageProperties> PardonAsync(int number)
    {
        var reason = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        return Replies.Ephemeral(await Moderation.Pardon.RunAsync(cases, access, Context.Guild!, (GuildUser)Context.User, number, reason));
    }
}
