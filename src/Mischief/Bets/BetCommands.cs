using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief.Bets;

[SlashCommand("bet", "Bet points on outcomes", Contexts = [InteractionContextType.Guild])]
public sealed class BetCommands(
    BetBook book,
    ModuleState modules,
    SettingsStore settings,
    AccessControl access,
    IDbContextFactory<BotDbContext> dbFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("create", "Start a bet; you earn a cut of the pool")]
    public async Task<InteractionMessageProperties> CreateAsync(
        [SlashCommandParameter(Description = "What are we betting on?", MaxLength = 200)] string question,
        [SlashCommandParameter(Description = "2 to 5 options, separated by commas", MaxLength = 400)] string options,
        [SlashCommandParameter(Name = "closes-in-hours", Description = "When betting stops", MinValue = 1, MaxValue = 8760)] int closesInHours)
    {
        if (await UnavailableAsync() is { } unavailable)
            return unavailable;

        var choices = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.Length > 80 ? o[..80] : o)
            .Distinct()
            .ToArray();
        if (choices.Length is < 2 or > BetBook.MaxOptions)
            return Replies.Ephemeral($"A bet needs 2 to {BetBook.MaxOptions} different options, separated by commas.");

        var rules = await settings.GetAsync<MischiefRules>(GuildId, MischiefCommands.ModuleId);
        if (closesInHours > rules.BetMaxDays * 24)
            return Replies.Ephemeral($"Bets close within {rules.BetMaxDays} days at most.");

        var bet = await book.CreateAsync(GuildId, Context.Channel.Id, Context.User.Id, question.Trim(), choices, TimeSpan.FromHours(closesInHours));
        var message = await Context.Channel.SendMessageAsync(await book.MessageAsync(bet));

        await using var db = await dbFactory.CreateDbContextAsync();
        db.Bets.Attach(bet);
        bet.MessageId = message.Id;
        await db.SaveChangesAsync();

        return Replies.Ephemeral($"Bet {bet.Id} is up. Resolve it with `/bet resolve` once the answer is known.");
    }

    [SubSlashCommand("resolve", "Declare the winning option (again, to change it)")]
    public async Task<InteractionMessageProperties> ResolveAsync(
        [SlashCommandParameter(Description = "Bet", AutocompleteProviderType = typeof(BetAutocomplete))] long bet,
        [SlashCommandParameter(Description = "The winning option", AutocompleteProviderType = typeof(BetOptionAutocomplete))] int winner)
    {
        if (await UnavailableAsync() is { } unavailable)
            return unavailable;
        if (await FindAsync(bet) is not { } found)
            return Replies.Ephemeral("There's no such bet.");
        if (winner < 0 || winner >= found.Options.Length)
            return Replies.Ephemeral("Pick the winner from the list.");

        var manager = await CanManageAsync();
        if (found.State != BetStates.Open && !manager)
            return Replies.Ephemeral($"That bet is already {found.State}. Changing it needs `{BotPermissions.ManageBets}`.");
        if (!manager && found.CreatorId != Context.User.Id)
            return Replies.Ephemeral($"Only <@{found.CreatorId}> or someone with `{BotPermissions.ManageBets}` can resolve it.");
        if (!manager && await book.HasStakedAsync(found.Id, found.CreatorId))
            return Replies.Ephemeral($"You have money on your own bet, so someone with `{BotPermissions.ManageBets}` has to resolve it.");

        return Public(await book.ResolveAsync(found.Id, winner, Context.User.Id), found);
    }

    [SubSlashCommand("cancel", "Call a bet off and refund every stake")]
    public async Task<InteractionMessageProperties> CancelAsync(
        [SlashCommandParameter(Description = "Bet", AutocompleteProviderType = typeof(BetAutocomplete))] long bet)
    {
        if (await UnavailableAsync() is { } unavailable)
            return unavailable;
        if (await FindAsync(bet) is not { } found)
            return Replies.Ephemeral("There's no such bet.");

        var manager = await CanManageAsync();
        if (found.State != BetStates.Open && !manager)
            return Replies.Ephemeral($"That bet is already {found.State}. Changing it needs `{BotPermissions.ManageBets}`.");
        if (!manager && found.CreatorId != Context.User.Id)
            return Replies.Ephemeral($"Only <@{found.CreatorId}> or someone with `{BotPermissions.ManageBets}` can cancel it.");

        return Public(await book.CancelAsync(found.Id, Context.User.Id), found);
    }

    [SubSlashCommand("revert", "Undo a resolution or cancellation: take back its payouts")]
    [RequirePermission(BotPermissions.ManageBets)]
    public async Task<InteractionMessageProperties> RevertAsync(
        [SlashCommandParameter(Description = "Bet", AutocompleteProviderType = typeof(BetAutocomplete))] long bet)
    {
        if (await FindAsync(bet) is not { } found)
            return Replies.Ephemeral("There's no such bet.");

        return Public(await book.RevertAsync(found.Id), found);
    }

    private async Task<InteractionMessageProperties?> UnavailableAsync()
    {
        if (!await modules.IsEnabledAsync(GuildId, MischiefCommands.ModuleId))
            return Replies.Ephemeral($"The `{MischiefCommands.ModuleId}` module is off.");
        if (!await modules.IsEnabledAsync(GuildId, PointsEngine.ModuleId))
            return Replies.Ephemeral($"Bets need the `{PointsEngine.ModuleId}` module.");
        return null;
    }

    private async Task<Bet?> FindAsync(long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Bets.FirstOrDefaultAsync(b => b.Id == id && b.GuildId == GuildId);
    }

    private ValueTask<bool> CanManageAsync() => access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.ManageBets);

    private InteractionMessageProperties Public(string result, Bet bet) => new()
    {
        Content = $"<@{Context.User.Id}>, bet {bet.Id} ({bet.Question}): {result}",
        AllowedMentions = AllowedMentionsProperties.None,
    };
}

// Pressing an option asks how much to stake.
public sealed class BetButtons : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("bet")]
    public InteractionCallbackProperties Stake(long betId, int option)
        => InteractionCallback.Modal(new ModalProperties($"betstake:{betId}:{option}", "Place your stake")
        {
            new LabelProperties("How many points?", new TextInputProperties("amount", TextInputStyle.Short)
            {
                MinLength = 1,
                MaxLength = 9,
                Placeholder = "e.g. 25",
            }),
        });
}

public sealed class BetStakeModal(BetBook book, PointsEngine points) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("betstake")]
    public async Task<InteractionMessageProperties> StakeAsync(long betId, int option)
    {
        var input = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        if (!int.TryParse(input.Trim(), out var amount) || amount <= 0)
            return Replies.Ephemeral("Stake a whole number of points, more than zero.");

        var error = await book.StakeAsync(betId, Context.User.Id, option, amount);
        return Replies.Ephemeral(error ?? $"You staked {points.Rules(Context.Guild!.Id).Format(amount)}.");
    }
}

public sealed class BetAutocomplete(IDbContextFactory<BotDbContext> dbFactory) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var guildId = context.Interaction.GuildId!.Value;
        await using var db = await dbFactory.CreateDbContextAsync();
        var bets = await db.Bets.Where(b => b.GuildId == guildId).OrderByDescending(b => b.Id).Take(100).ToListAsync();

        return bets
            .Where(b => b.Question.Contains(input, StringComparison.OrdinalIgnoreCase) || b.Id.ToString() == input)
            .Take(25)
            .Select(b => (b.Id, Label: $"{b.Id}: {b.Question} ({b.State})"))
            .Select(b => new ApplicationCommandOptionChoiceProperties(b.Label.Length <= 100 ? b.Label : b.Label[..100], b.Id));
    }
}

// Lists the options of the bet picked in the same command.
public sealed class BetOptionAutocomplete(IDbContextFactory<BotDbContext> dbFactory) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var picked = context.Interaction.Data.Options
            .SelectMany(o => o.Options ?? [])
            .FirstOrDefault(o => o.Name == "bet")?.Value;
        if (!long.TryParse(picked, out var betId))
            return [];

        await using var db = await dbFactory.CreateDbContextAsync();
        var bet = await db.Bets.FindAsync(betId);
        return bet?.Options.Select((name, i) => new ApplicationCommandOptionChoiceProperties(name, i)) ?? [];
    }
}
