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
    public async Task ResolveAsync(
        [SlashCommandParameter(Description = "Bet", AutocompleteProviderType = typeof(BetAutocomplete))] long bet,
        [SlashCommandParameter(Description = "The winning option", AutocompleteProviderType = typeof(BetOptionAutocomplete))] int winner)
    {
        var found = await FindAsync(bet);
        var refusal = await UnavailableAsync()
            ?? (found is null ? Replies.Ephemeral("There's no such bet.") : null)
            ?? (winner < 0 || winner >= found!.Options.Length ? Replies.Ephemeral("Pick the winner from the list.") : null)
            ?? await ChangeRefusalAsync(found!, "resolve");
        if (refusal is null && !await CanManageAsync() && await book.HasStakedAsync(found!.Id, found.CreatorId))
            refusal = Replies.Ephemeral($"You have money on your own bet, so someone with `{BotPermissions.ManageBets}` has to resolve it.");

        if (refusal is not null)
            await RespondAsync(InteractionCallback.Message(refusal));
        else
            await PublicDeferredAsync(() => book.ResolveAsync(found!.Id, winner, Context.User.Id), found!);
    }

    [SubSlashCommand("cancel", "Call a bet off and refund every stake")]
    public async Task CancelAsync(
        [SlashCommandParameter(Description = "Bet", AutocompleteProviderType = typeof(BetAutocomplete))] long bet)
    {
        var found = await FindAsync(bet);
        var refusal = await UnavailableAsync()
            ?? (found is null ? Replies.Ephemeral("There's no such bet.") : null)
            ?? await ChangeRefusalAsync(found!, "cancel");

        if (refusal is not null)
            await RespondAsync(InteractionCallback.Message(refusal));
        else
            await PublicDeferredAsync(() => book.CancelAsync(found!.Id, Context.User.Id), found!);
    }

    [SubSlashCommand("revert", "Undo a resolution or cancellation: take back its payouts")]
    [RequirePermission(BotPermissions.ManageBets)]
    public async Task RevertAsync(
        [SlashCommandParameter(Description = "Bet", AutocompleteProviderType = typeof(BetAutocomplete))] long bet)
    {
        if (await FindAsync(bet) is not { } found)
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("There's no such bet.")));
        else
            await PublicDeferredAsync(() => book.RevertAsync(found.Id), found);
    }

    // Creators may resolve or cancel their own open bets; anything else needs bets.manage.
    private async Task<InteractionMessageProperties?> ChangeRefusalAsync(Bet bet, string verb)
    {
        if (await CanManageAsync())
            return null;
        if (bet.State != BetStates.Open)
            return Replies.Ephemeral($"That bet is already {bet.State}. Changing it needs `{BotPermissions.ManageBets}`.");
        if (bet.CreatorId != Context.User.Id)
            return Replies.Ephemeral($"Only <@{bet.CreatorId}> or someone with `{BotPermissions.ManageBets}` can {verb} it.");
        return null;
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

    // Paying out touches several balances and the bet message; defer so Discord doesn't give up waiting.
    private async Task PublicDeferredAsync(Func<Task<string>> work, Bet bet)
    {
        await RespondAsync(InteractionCallback.DeferredMessage());
        var result = await work();
        await ModifyResponseAsync(m =>
        {
            m.Content = $"<@{Context.User.Id}>, bet {bet.Id} ({bet.Question}): {result}";
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }
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
