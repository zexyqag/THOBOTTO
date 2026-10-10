using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

// What /rename and /name share: the mischief itself (MischiefActions), and replying in public.
public abstract class MischiefModule(
    MischiefActions actions,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = "mischief";

    protected MischiefActions Actions { get; } = actions;

    protected ModuleState Modules { get; } = modules;

    protected SettingsStore Settings { get; } = settings;

    protected PointsEngine Points { get; } = points;

    protected IDbContextFactory<BotDbContext> DbFactory { get; } = dbFactory;

    protected TimeProvider Time { get; } = time;

    protected Guild Guild => Context.Guild!;

    protected MischiefActor Actor => new(Guild.Id, Context.User.Id, Context.User.Username, Context.Channel.Id);

    // Mischief calls Discord (nicknames, roles) and sends DMs, which can take longer than the 3 seconds
    // Discord waits. So: defer privately; refusals fill that in. Public announcements go to the channel
    // as ordinary messages (a follow-up would take over the private placeholder and stay private),
    // and the placeholder is removed.
    protected async Task RunAsync(Func<Task<MischiefResult>> act)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await act();
        if (!result.Public)
        {
            await ModifyResponseAsync(m => m.Content = result.Text);
            return;
        }
        await Context.Channel.SendMessageAsync(new() { Content = result.Text, AllowedMentions = AllowedMentionsProperties.None });
        await DeleteResponseAsync();
    }

    protected async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await Actions.OffAsync(Guild.Id) is { } off ? Replies.Ephemeral(off.Text) : null;
}

public sealed class PaintColourAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var choices = PaintColours.Named.Keys
            .Where(name => name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Select(name => new ApplicationCommandOptionChoiceProperties(name, name));
        return new(choices);
    }
}
