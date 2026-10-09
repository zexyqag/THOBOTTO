using System.Buffers.Text;
using System.Text;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

namespace THOBOTTO.Stats;

[SlashCommand("wrapped", "Music Wrapped: yours, the server's, or a helper's", Contexts = [InteractionContextType.Guild])]
public sealed class WrappedCommands(WrappedService wrapped) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("me", "Your Wrapped (only you see it, unless you share it)")]
    public async Task MeAsync([SlashCommandParameter(Description = "When")] WrappedPeriod period = WrappedPeriod.ThisYear)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var card = await WrappedEmbeds.BuildAsync(wrapped, Context.Guild!, "me", Context.User.Id.ToString(), period, (GuildUser)Context.User);
        await ModifyResponseAsync(m => WrappedEmbeds.Fill(m, card, WrappedEmbeds.ShareId("me", period, Context.User.Id.ToString())));
    }

    [SubSlashCommand("server", "The server's music Wrapped")]
    public async Task ServerAsync([SlashCommandParameter(Description = "When")] WrappedPeriod period = WrappedPeriod.ThisYear)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var card = await WrappedEmbeds.BuildAsync(wrapped, Context.Guild!, "server", "", period, (GuildUser)Context.User);
        await ModifyResponseAsync(m => WrappedEmbeds.Fill(m, card, WrappedEmbeds.ShareId("server", period, "all")));
    }

    [SubSlashCommand("helper", "A helper's Wrapped, in its own voice")]
    public async Task HelperAsync(
        [SlashCommandParameter(Description = "Which one", AutocompleteProviderType = typeof(VoiceAutocomplete))] string voice,
        [SlashCommandParameter(Description = "When")] WrappedPeriod period = WrappedPeriod.ThisYear)
    {
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
    public static async Task<WrappedCard?> BuildAsync(WrappedService wrapped, Guild guild, string kind, string key, WrappedPeriod period, GuildUser viewer)
    {
        // In Discord, members are mentions: shown as names, nobody pinged.
        static string Mention(ulong id) => $"<@{id}>";
        return kind switch
        {
            "me" when ulong.TryParse(key, out var userId) => await wrapped.MemberAsync(guild, userId,
                $"{(userId == viewer.Id ? viewer.Nickname ?? viewer.GlobalName ?? viewer.Username : Mention(userId))}'s", period, Mention),
            "server" => await wrapped.ServerAsync(guild, period, Mention),
            "voice" => await wrapped.VoiceAsync(guild, key, period, Mention),
            _ => null,
        };
    }

    public static void Fill(MessageOptions message, WrappedCard? card, string? shareId)
    {
        message.AllowedMentions = AllowedMentionsProperties.None;
        if (card is null)
        {
            message.Content = "There's no such helper voice here; pick one from the list.";
            return;
        }
        message.Embeds = [new()
        {
            Title = card.Title,
            Description = card.Intro,
            Color = card.Color is { } c ? new(c) : default,
            Fields = [
                .. card.Facts.Select(f => new EmbedFieldProperties { Name = f.Label, Value = Clip(f.Value), Inline = true }),
                .. card.Lists.Select(l => new EmbedFieldProperties { Name = l.Heading, Value = Clip(string.Join('\n', l.Lines)) }),
            ],
        }];
        message.Components = shareId is null ? [] : [new ActionRowProperties { new ButtonProperties(shareId, "Share", EmojiProperties.Standard("📣"), ButtonStyle.Secondary) }];
    }

    // The target can hold any character (personality names), so it travels base64url-encoded.
    public static string ShareId(string kind, WrappedPeriod period, string key) => $"wrapped:{kind}:{(int)period}:{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key))}";

    public static string Decode(string target) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(target));

    private static string Clip(string text) => text.Length <= 1024 ? text : text[..1020] + "…";
}
