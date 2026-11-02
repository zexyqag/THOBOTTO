using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief.Bets;

// Right-click a message, Apps, Bet on this: /bet create, the message as its question.
public sealed class BetMenu(ModuleState modules) : GuildCommandModule
{
    [MessageCommand("Bet on this", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> BetAsync(RestMessage message)
    {
        if (await BetMenuModal.UnavailableAsync(modules, GuildId) is { } off)
            return InteractionCallback.Message(Replies.Ephemeral(off));
        var question = message.Content.Trim();
        return InteractionCallback.Modal(new ModalProperties("betonthis", "Bet on this")
        {
            new LabelProperties("What are we betting on?", new TextInputProperties("question", TextInputStyle.Short)
            {
                Value = question.Length == 0 ? null : question.Length <= 200 ? question : question[..199] + "…",
                MaxLength = 200,
            }),
            new LabelProperties($"2 to {BetBook.MaxOptions} options, separated by commas", new TextInputProperties("options", TextInputStyle.Short) { Value = "Yes, No", MaxLength = 400 }),
            new LabelProperties("Betting stops after (hours)", new TextInputProperties("hours", TextInputStyle.Short) { Value = "24", MaxLength = 4 }),
        });
    }
}

public sealed class BetMenuModal(BetBook book, ModuleState modules, SettingsStore settings) : GuildModalModule
{
    public static async Task<string?> UnavailableAsync(ModuleState modules, ulong guildId)
        => !await modules.IsEnabledAsync(guildId, MischiefModule.ModuleId) ? ModuleRegistry.Off(MischiefModule.ModuleId)
            : !await modules.IsEnabledAsync(guildId, PointsEngine.ModuleId) ? ModuleRegistry.Off(PointsEngine.ModuleId)
            : null;

    [ComponentInteraction("betonthis")]
    public async Task<InteractionCallbackProperties> CreateAsync()
    {
        var inputs = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());
        var choices = inputs["options"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.Length > 80 ? o[..80] : o).Distinct().ToArray();
        var rules = await settings.GetAsync<MischiefRules>(GuildId, MischiefModule.ModuleId);
        var refusal = await UnavailableAsync(modules, GuildId)
            ?? (inputs["question"].Length == 0 ? "Say what we're betting on."
            : choices.Length is < 2 or > BetBook.MaxOptions ? $"A bet needs 2 to {BetBook.MaxOptions} different options, separated by commas."
            : !int.TryParse(inputs["hours"], out var hours) || hours < 1 ? "Betting stops after a whole number of hours, at least 1."
            : hours > rules.BetMaxDays * 24 ? $"Bets close within {rules.BetMaxDays} days at most."
            : null);
        if (refusal is not null)
            return InteractionCallback.Message(Replies.Ephemeral(refusal));
        var bet = await book.CreateAsync(GuildId, Context.Channel.Id, Context.User.Id, inputs["question"], choices, TimeSpan.FromHours(int.Parse(inputs["hours"])));
        var posted = await Context.Channel.SendMessageAsync(await book.MessageAsync(bet));
        await book.PostedAsync(bet, posted.Id);
        return InteractionCallback.Message(Replies.Ephemeral($"Bet {bet.Id} is up. Resolve it with `/bet resolve` once the answer is known."));
    }
}
