using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;

namespace THOBOTTO.Commands;

// Everything a moderator does, in one place. Each module adds its group or command from its own folder (as part
// of this class, hence in this namespace). Hidden by default from members without Moderate Members; the server
// can show it to other roles in its Integrations settings. The bot's own permission checks apply either way.
[SlashCommand("mod", "Moderation", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
public sealed partial class ModCommands(ModuleState modules, CaseBook cases, ModActions actions, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
    : ModModule(modules)
{
    // Changing a case is for whoever made it, or mod.manage.
    internal static async Task<string?> CaseRefusalAsync(CaseBook cases, AccessControl access, ApplicationCommandContext context, int number)
    {
        if (await cases.FindAsync(context.Guild!.Id, number) is not { } c)
            return "There's no such case.";
        return c.ModeratorId == context.User.Id || await access.CanAsync(context.Guild, (GuildUser)context.User, BotPermissions.ModManage)
            ? null
            : $"Only <@{c.ModeratorId}> or someone with `{BotPermissions.ModManage}` can change it.";
    }

    private ModCase NewCase(string type, ulong targetId, string reason) => new()
    {
        GuildId = Guild.Id,
        Type = type,
        TargetId = targetId,
        ModeratorId = Actor.Id,
        Reason = reason.Trim(),
        CreatedAt = time.GetUtcNow(),
    };
}
