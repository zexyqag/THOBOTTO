using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

[SlashCommand("name", "Names: shields, locks, colours, history, prices", Contexts = [InteractionContextType.Guild])]
public sealed partial class NameCommands(
    MischiefActions actions,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : MischiefModule(actions, modules, settings, points, dbFactory, time)
{
    [SubSlashCommand("shield", "Nobody can rename or paint you for a while")]
    public Task ShieldAsync(
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
        => RunAsync(() => Actions.ShieldAsync(Actor, hours));

    [SubSlashCommand("lock", "Keep someone's current name for a while")]
    public Task LockAsync(
        [SlashCommandParameter(Description = "Whose name to lock")] GuildUser user,
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
        => RunAsync(() => Actions.LockAsync(Actor, user, hours));

    [SubSlashCommand("unlock", "Break a name lock (costs more than the lock did)")]
    public Task UnlockAsync(
        [SlashCommandParameter(Description = "Whose name to unlock (you if left out)")] GuildUser? user = null)
        => RunAsync(() => Actions.UnlockAsync(Actor, user?.Id ?? Context.User.Id));

    [SubSlashCommand("buyback", "Buy your own name back (resets your nickname)")]
    public Task BuyBackAsync()
        => RunAsync(() => Actions.BuyBackAsync(Actor, (GuildUser)Context.User));

    [SubSlashCommand("colour", "Give someone a name colour for a while")]
    public Task PaintAsync(
        [SlashCommandParameter(Description = "Who to paint")] GuildUser user,
        [SlashCommandParameter(Description = "A colour name or a hex code like #ff00ff", AutocompleteProviderType = typeof(PaintColourAutocomplete))] string colour,
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
        => RunAsync(() => Actions.PaintAsync(Actor, user, colour, hours));

    [SubSlashCommand("uncolour", "Remove a name colour early (costs more than colouring did)")]
    public Task UnpaintAsync(
        [SlashCommandParameter(Description = "Who to clean up (you if left out)")] GuildUser? user = null)
        => RunAsync(() => Actions.UnpaintAsync(Actor, user?.Id ?? Context.User.Id));
}
