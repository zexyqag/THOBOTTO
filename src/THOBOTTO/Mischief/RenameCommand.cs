
using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

public sealed class RenameCommand(
    MischiefActions actions,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : MischiefModule(actions, modules, settings, points, dbFactory, time)
{
    [SlashCommand("rename", "Rename someone (yourself costs extra)", Contexts = [InteractionContextType.Guild])]
    public Task RenameAsync(
        [SlashCommandParameter(Description = "Who to rename")] GuildUser user,
        [SlashCommandParameter(Description = "New nickname (leave out to reset it)", MaxLength = 32)] string? name = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 200)] string? reason = null)
        => RunAsync(() => Actions.RenameAsync(Actor, user, name, reason));
}
