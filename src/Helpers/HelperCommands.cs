using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Helpers;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("helpers", "Helper bots' personalities here; the panel does more (needs helpers.manage)")]
    [RequirePermission(BotPermissions.ManageHelpers)]
    public sealed class HelperSetup(HelperFleet fleet, PersonalityBook book) : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Guild!.Id;

        [SubSlashCommand("list", "The helpers here and the personality each wears")]
        public async Task<InteractionMessageProperties> ListAsync()
        {
            var here = fleet.Helpers.Where(h => h.InGuild(GuildId)).ToList();
            if (here.Count == 0)
                return Replies.Ephemeral("No helper is in this server yet; the web panel has invite links.");
            var lines = new List<string>();
            foreach (var helper in here)
                lines.Add($"<@{helper.UserId}>: {(await book.WornAsync(GuildId, helper.UserId))?.Name ?? "no personality"}");
            var all = await book.ListAsync(GuildId);
            return Replies.Ephemeral(string.Join('\n', lines) + $"\n\nPersonalities here: {(all.Count == 0 ? "none yet (`/setup helpers create`)" : string.Join(", ", all.Select(p => p.Name)))}");
        }

        [SubSlashCommand("create", "A new personality for this server, blank or from a template")]
        public async Task<InteractionMessageProperties> CreateAsync(
            [SlashCommandParameter(Description = "Its name; helpers wearing it go by this here", MaxLength = 32)] string name,
            [SlashCommandParameter(Description = "Start from")] PersonalityTemplate template = PersonalityTemplate.Blank)
        {
            var from = template == PersonalityTemplate.Blank ? null : Template.All[(int)template - 1];
            var created = await book.CreateAsync(GuildId, name.Trim(), from, Context.User.Id);
            return Replies.Ephemeral($"Created **{created.Name}**. Give it to a helper with `/setup helpers assign`; the web panel edits its look and lines.");
        }

        [SubSlashCommand("assign", "Put a personality on a helper here (leave it out to take it off)")]
        public async Task<InteractionMessageProperties> AssignAsync(
            [SlashCommandParameter(Description = "Helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string helper,
            [SlashCommandParameter(Description = "Personality", AutocompleteProviderType = typeof(PersonalityAutocomplete))] long? personality = null)
        {
            if (!ulong.TryParse(helper, out var helperId) || fleet.Helpers.FirstOrDefault(h => h.UserId == helperId) is not { } bot)
                return Replies.Ephemeral("There's no such helper.");
            if (personality is { } id && await book.FindAsync(GuildId, id) is null)
                return Replies.Ephemeral("There's no such personality here.");
            await book.AssignAsync(GuildId, bot.UserId, personality, Context.User.Id);
            var worn = await book.WornAsync(GuildId, bot.UserId);
            return Replies.Ephemeral(worn is null ? $"<@{bot.UserId}> wears no personality here now." : $"<@{bot.UserId}> is **{worn.Name}** here now.");
        }

        [SubSlashCommand("phrases", "What a personality says at a moment: list, add, remove, or clear")]
        public async Task<InteractionMessageProperties> PhrasesAsync(
            [SlashCommandParameter(Description = "Personality", AutocompleteProviderType = typeof(PersonalityAutocomplete))] long personality,
            [SlashCommandParameter(Description = "When it says it")] Moment moment,
            [SlashCommandParameter(Description = "What to do")] PhraseAction action = PhraseAction.List,
            [SlashCommandParameter(Description = "The phrase to add, or the number to remove; {track} {user} {channel} {helper} get filled in", MaxLength = 300)] string? text = null)
        {
            if (await book.FindAsync(GuildId, personality) is not { } found)
                return Replies.Ephemeral("There's no such personality here.");
            var key = moment.ToString().ToLowerInvariant();
            var current = found.Phrases.GetValueOrDefault(key) ?? [];
            switch (action)
            {
                case PhraseAction.Add when !string.IsNullOrWhiteSpace(text):
                    current = [.. current, text.Trim()];
                    break;
                case PhraseAction.Remove when int.TryParse(text, out var n) && n >= 1 && n <= current.Count:
                    current = current.Where((_, i) => i != n - 1).ToList();
                    break;
                case PhraseAction.Clear:
                    current = [];
                    break;
                case PhraseAction.Add or PhraseAction.Remove:
                    return Replies.Ephemeral(action == PhraseAction.Add ? "Give the phrase in `text`." : "Give the number to remove in `text`.");
            }
            if (action != PhraseAction.List)
                await book.ChangeAsync(GuildId, found.Id, Context.User.Id, $"{key} phrases", p => p.Phrases[key] = current);

            return new()
            {
                Content = $"**{found.Name}**, {key}:\n" + (current.Count == 0 ? "-# none: plain lines are used" : string.Join('\n', current.Select((p, i) => $"{i + 1}. {p}"))),
                Flags = MessageFlags.Ephemeral,
                AllowedMentions = AllowedMentionsProperties.None,
            };
        }
    }
}

public enum PersonalityTemplate
{
    Blank,
    [SlashCommandChoice(Name = "DJ Volume")]
    DjVolume,
    Jeeves,
}

public enum Moment
{
    Joined,
    Playing,
    Skipped,
    Stopped,
    Finished,
    Lonely,
}

public enum PhraseAction
{
    List,
    Add,
    Remove,
    Clear,
}

public sealed class HelperAutocomplete(HelperFleet fleet, PersonalityBook book) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var guildId = context.Interaction.GuildId!.Value;
        var choices = new List<ApplicationCommandOptionChoiceProperties>();
        foreach (var helper in fleet.Helpers.Where(h => h.InGuild(guildId)))
            choices.Add(new($"{await book.NameAsync(guildId, helper)} ({helper.Name})", helper.UserId.ToString()));
        return choices;
    }
}

public sealed class PersonalityAutocomplete(PersonalityBook book) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return (await book.ListAsync(context.Interaction.GuildId!.Value))
            .Where(p => p.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new ApplicationCommandOptionChoiceProperties(p.Name, p.Id));
    }
}
