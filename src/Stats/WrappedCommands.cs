using System.Buffers.Text;
using System.Text;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Modules;

namespace THOBOTTO.Stats;

[SlashCommand("wrapped", "Music Wrapped: yours, the server's, or a helper's", Contexts = [InteractionContextType.Guild])]
public sealed class WrappedCommands(WrappedService wrapped, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
{
    // Answers for it when the module is off.
    private async Task<bool> OffAsync()
    {
        if (await modules.IsEnabledAsync(Context.Guild!.Id, WrappedPoster.ModuleId))
            return false;
        await RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"The `{WrappedPoster.ModuleId}` module is off.")));
        return true;
    }

    [SubSlashCommand("me", "Your Wrapped (only you see it, unless you share it)")]
    public async Task MeAsync([SlashCommandParameter(Description = "When")] WrappedPeriod period = WrappedPeriod.ThisYear)
    {
        if (await OffAsync())
            return;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var card = await WrappedEmbeds.BuildAsync(wrapped, Context.Guild!, "me", Context.User.Id.ToString(), period, (GuildUser)Context.User);
        await ModifyResponseAsync(m => WrappedEmbeds.Fill(m, card, WrappedEmbeds.ShareId("me", period, Context.User.Id.ToString())));
    }

    [SubSlashCommand("server", "The server's music Wrapped")]
    public async Task ServerAsync([SlashCommandParameter(Description = "When")] WrappedPeriod period = WrappedPeriod.ThisYear)
    {
        if (await OffAsync())
            return;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var card = await WrappedEmbeds.BuildAsync(wrapped, Context.Guild!, "server", "", period, (GuildUser)Context.User);
        await ModifyResponseAsync(m => WrappedEmbeds.Fill(m, card, WrappedEmbeds.ShareId("server", period, "all")));
    }

    [SubSlashCommand("helper", "A helper's Wrapped, in its own voice")]
    public async Task HelperAsync(
        [SlashCommandParameter(Description = "Which one", AutocompleteProviderType = typeof(VoiceAutocomplete))] string voice,
        [SlashCommandParameter(Description = "When")] WrappedPeriod period = WrappedPeriod.ThisYear)
    {
        if (await OffAsync())
            return;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var card = await WrappedEmbeds.BuildAsync(wrapped, Context.Guild!, "voice", voice, period, (GuildUser)Context.User);
        await ModifyResponseAsync(m => WrappedEmbeds.Fill(m, card, card is null ? null : WrappedEmbeds.ShareId("voice", period, voice)));
    }
}

// The Share button: posts the same Wrapped in the channel for everyone.
public sealed class WrappedShare(WrappedService wrapped) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("wrapped")]
    public async Task ShareAsync(string kind, int period, string target)
    {
        var key = WrappedEmbeds.Decode(target);
        if (kind == "me" && key != Context.User.Id.ToString())
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Only its owner can share a Wrapped.")));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage());
        var card = await WrappedEmbeds.BuildAsync(wrapped, Context.Guild!, kind, key, (WrappedPeriod)period, (GuildUser)Context.User);
        await ModifyResponseAsync(m => WrappedEmbeds.Fill(m, card, null));
    }
}

public sealed class VoiceAutocomplete(WrappedService wrapped) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return (await wrapped.VoicesAsync(context.Interaction.GuildId!.Value))
            .Where(v => v.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(v => new ApplicationCommandOptionChoiceProperties(v.Name, v.Key));
    }
}

public static class WrappedEmbeds
{
    public static async Task<IReadOnlyList<WrappedCard>?> BuildAsync(WrappedService wrapped, Guild guild, string kind, string key, WrappedPeriod period, GuildUser viewer)
    {
        // In Discord, members are mentions: shown as names, nobody pinged.
        static string Mention(ulong id) => $"<@{id}>";
        return kind switch
        {
            "me" when ulong.TryParse(key, out var userId) => await wrapped.MemberAsync(guild, userId,
                $"{(userId == viewer.Id ? viewer.Nickname ?? viewer.GlobalName ?? viewer.Username : Mention(userId))}'s", period, Mention),
            "server" => await wrapped.ServerAsync(guild, period, Mention),
            "voice" => await wrapped.VoiceAsync(guild, key, period, Mention) is { } card ? [card] : null,
            _ => null,
        };
    }

    // Discord allows 6000 characters across a message's embeds; the last lists give way first.
    private const int EmbedBudget = 5800;

    public static void Fill(MessageOptions message, IReadOnlyList<WrappedCard>? cards, string? shareId)
    {
        message.AllowedMentions = AllowedMentionsProperties.None;
        if (cards is null)
        {
            message.Content = "There's no such helper voice here; pick one from the list.";
            return;
        }
        message.Embeds = Embeds(cards);
        message.Components = shareId is null ? [] : [new ActionRowProperties { new ButtonProperties(shareId, "Share", EmojiProperties.Standard("📣"), ButtonStyle.Secondary) }];
    }

    public static List<EmbedProperties> Embeds(IReadOnlyList<WrappedCard> cards)
    {
        var used = 0;
        return cards.Select(card =>
        {
            var fields = card.Facts.Select(f => new EmbedFieldProperties { Name = f.Label, Value = Clip(f.Value), Inline = true }).ToList();
            used += card.Title.Length + (card.Intro?.Length ?? 0) + fields.Sum(f => f.Name!.Length + f.Value!.Length);
            foreach (var (heading, lines) in card.Lists)
            {
                var value = Clip(string.Join('\n', lines));
                if (used + heading.Length + value.Length > EmbedBudget)
                    break;
                used += heading.Length + value.Length;
                fields.Add(new() { Name = heading, Value = value });
            }
            return new EmbedProperties { Title = card.Title, Description = card.Intro, Color = card.Color is { } c ? new(c) : default, Fields = fields };
        }).ToList();
    }

    // The target can hold any character (personality names), so it travels base64url-encoded.
    public static string ShareId(string kind, WrappedPeriod period, string key) => $"wrapped:{kind}:{(int)period}:{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key))}";

    public static string Decode(string target) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(target));

    private static string Clip(string text) => text.Length <= 1024 ? text : text[..1020] + "…";
}
