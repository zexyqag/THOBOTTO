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

public sealed class ModUserCommands(CaseBook cases) : ApplicationCommandModule<ApplicationCommandContext>
{
    [UserCommand("Mod history", Contexts = [InteractionContextType.Guild])]
    [RequirePermission(BotPermissions.ModWarn)]
    public Task<InteractionMessageProperties> HistoryAsync(User user) => History.ForAsync(cases, Context.Guild!.Id, user);
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

public sealed class ModPardonModal(CaseBook cases, AccessControl access) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("modpardonwhy")]
    public async Task<InteractionMessageProperties> PardonAsync(int number)
    {
        var reason = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        return Replies.Ephemeral(await Moderation.Pardon.RunAsync(cases, access, Context.Guild!, (GuildUser)Context.User, number, reason));
    }
}
