using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Quotes;

public sealed class QuoteCommands(QuoteBook book, ModuleState modules, RestClient rest) : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = "quotes";

    private Guild Guild => Context.Guild!;

    [MessageCommand("Quote this", Contexts = [InteractionContextType.Guild])]
    public Task<InteractionMessageProperties> QuoteThisAsync(RestMessage message) => FromMessagesAsync([message]);

    // The target of a message command arrives without the message it replied to, so fetch that.
    [MessageCommand("Quote with reply", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> QuoteWithReplyAsync(RestMessage message)
    {
        if (message.MessageReference is not { Type: MessageReferenceType.Reply } reference)
            return await FromMessagesAsync([message]);

        var setup = await rest.GetMessageAsync(reference.ChannelId, reference.MessageId);
        return await FromMessagesAsync([setup, message]);
    }

    private async Task<InteractionMessageProperties> FromMessagesAsync(IReadOnlyList<RestMessage> messages)
    {
        if (!await modules.IsEnabledAsync(Guild.Id, ModuleId))
            return Replies.Ephemeral($"The `{ModuleId}` module is off.");
        if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
            return Replies.Ephemeral("Only text can be quoted, and one of those messages has none.");

        var last = messages[^1];
        var quote = await book.AddAsync(new()
        {
            GuildId = Guild.Id,
            AddedById = Context.User.Id,
            ChannelId = last.ChannelId,
            MessageId = last.Id,
            SaidAt = messages[0].CreatedAt,
            Lines = messages.Select((m, i) => new QuoteLine { Position = i, SpeakerId = m.Author.Id, Text = m.Content }).ToList(),
        });

        return Public(quote, $"<@{Context.User.Id}> saved quote #{quote.Id}");
    }

    public static InteractionMessageProperties Public(Quote quote, string? content = null) => new()
    {
        Content = content,
        Embeds = [Render(quote)],
        AllowedMentions = AllowedMentionsProperties.None,
    };

    public static EmbedProperties Render(Quote quote)
    {
        var text = new StringBuilder();
        if (quote.Context is { } context)
            text.AppendLine($"*{context}*").AppendLine();
        foreach (var line in quote.Lines.OrderBy(l => l.Position))
            text.AppendLine($"**{Speaker(line)}**: {line.Text}");

        text.AppendLine().Append($"-# #{quote.Id} · added by <@{quote.AddedById}> · <t:{quote.SaidAt.ToUnixTimeSeconds()}:D>");
        if (quote.MessageId is { } messageId)
            text.Append($" · [original](https://discord.com/channels/{quote.GuildId}/{quote.ChannelId}/{messageId})");

        return new() { Description = text.ToString(), Color = new(0x5865F2) };
    }

    public static string Speaker(QuoteLine line) => line.SpeakerId is { } id ? $"<@{id}>" : line.SpeakerName!;
}

public sealed class QuoteAddModal(QuoteBook book, RestClient rest, TimeProvider time) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("quoteadd")]
    public async Task<InteractionMessageProperties> AddAsync()
    {
        var inputs = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value);
        if (QuoteText.Parse(inputs["lines"]) is not { } lines)
            return Replies.Ephemeral($"Write one turn per line as `Name: what they said`, at most {QuoteText.MaxLines} lines.");

        var guildId = Context.Guild!.Id;
        var quoteLines = new List<QuoteLine>();
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
        {
            var member = await FindMemberAsync(guildId, line.Speaker);
            quoteLines.Add(new() { Position = i, SpeakerId = member, SpeakerName = member is null ? line.Speaker : null, Text = line.Text });
        }

        var context = inputs.GetValueOrDefault("context");
        var now = time.GetUtcNow();
        var quote = await book.AddAsync(new()
        {
            GuildId = guildId,
            AddedById = Context.User.Id,
            Context = string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
            SaidAt = now,
            Lines = quoteLines,
        });

        return QuoteCommands.Public(quote, $"<@{Context.User.Id}> saved quote #{quote.Id}");
    }

    // An exact (case-insensitive) match on nickname, display name or username; otherwise the name stays text.
    private async Task<ulong?> FindMemberAsync(ulong guildId, string name)
    {
        var candidates = await rest.FindGuildUserAsync(guildId, name, 10);
        return candidates.FirstOrDefault(u =>
            string.Equals(u.Nickname, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.GlobalName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}

[SlashCommand("quote", "The quote book", Contexts = [InteractionContextType.Guild])]
public sealed class QuoteGroup(QuoteBook book, ModuleState modules, AccessControl access) : ApplicationCommandModule<ApplicationCommandContext>
{
    private Guild Guild => Context.Guild!;

    [SubSlashCommand("add", "Save something said in voice (or anywhere)")]
    public async Task<InteractionCallbackProperties> AddAsync()
    {
        if (!await modules.IsEnabledAsync(Guild.Id, QuoteCommands.ModuleId))
            return InteractionCallback.Message(Replies.Ephemeral($"The `{QuoteCommands.ModuleId}` module is off."));

        return InteractionCallback.Modal(new ModalProperties("quoteadd", "Add a quote")
        {
            new LabelProperties("Who said what, one per line", new TextInputProperties("lines", TextInputStyle.Paragraph)
            {
                Placeholder = "Marty: I'll just check the basement\ngoldfish: please don't",
                MaxLength = 2000,
            })
            {
                Description = "Write it as Name: what they said",
            },
            new LabelProperties("Context (optional)", new TextInputProperties("context", TextInputStyle.Short)
            {
                Required = false,
                MaxLength = 200,
                Placeholder = "During the third wipe on the PZ server",
            }),
        });
    }

    [SubSlashCommand("random", "Post a random quote")]
    public async Task<InteractionMessageProperties> RandomAsync(
        [SlashCommandParameter(Description = "Only quotes with this member in them")] GuildUser? user = null)
    {
        if (!await modules.IsEnabledAsync(Guild.Id, QuoteCommands.ModuleId))
            return Replies.Ephemeral($"The `{QuoteCommands.ModuleId}` module is off.");

        return await book.RandomAsync(Guild.Id, user?.Id) is { } quote
            ? QuoteCommands.Public(quote)
            : Replies.Ephemeral(user is null ? "The quote book is empty." : $"There are no quotes with <@{user.Id}> yet.");
    }

    [SubSlashCommand("show", "Show a quote by number")]
    public async Task<InteractionMessageProperties> ShowAsync([SlashCommandParameter(Description = "Quote number")] long id)
        => await book.FindAsync(Guild.Id, id) is { } quote ? QuoteCommands.Public(quote) : Replies.Ephemeral($"There's no quote #{id}.");

    [SubSlashCommand("search", "Find quotes containing some text")]
    public async Task<InteractionMessageProperties> SearchAsync([SlashCommandParameter(Description = "Text to look for", MinLength = 2, MaxLength = 100)] string text)
    {
        var found = await book.SearchAsync(Guild.Id, text);
        if (found.Count == 0)
            return Replies.Ephemeral("No quotes match.");

        return Replies.Ephemeral(string.Join('\n', found.Select(q =>
        {
            var first = q.Lines.OrderBy(l => l.Position).First();
            var snippet = first.Text.Length > 80 ? first.Text[..80] + "…" : first.Text;
            return $"#{q.Id} {QuoteCommands.Speaker(first)}: {snippet}{(q.Lines.Count > 1 ? $" (+{q.Lines.Count - 1})" : "")}";
        })));
    }

    [SubSlashCommand("delete", "Remove a quote (yours, or any with quotes.manage)")]
    public async Task<InteractionMessageProperties> DeleteAsync([SlashCommandParameter(Description = "Quote number")] long id)
    {
        if (await book.FindAsync(Guild.Id, id) is not { } quote)
            return Replies.Ephemeral($"There's no quote #{id}.");
        if (quote.AddedById != Context.User.Id && !await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageQuotes))
            return Replies.Ephemeral($"Only <@{quote.AddedById}> or someone with `{BotPermissions.ManageQuotes}` can delete it.");

        await book.DeleteAsync(quote, Context.User.Id);
        return Replies.Ephemeral($"Deleted quote #{id}.");
    }
}
