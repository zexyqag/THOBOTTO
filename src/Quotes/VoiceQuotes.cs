using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Assistant;
using THOBOTTO.Helpers;
using THOBOTTO.Listening;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Notifications;

namespace THOBOTTO.Quotes;

// "Jeeves, quote that": a draft of the last words said, from the transcript kept in memory. Whoever asked grows
// it a line back, drops its first line, corrects what speech to text got wrong, and saves it, with the buttons or
// by saying so shortly after. The people quoted are told, and may delete it. Members who turned quoting off
// aren't quoted by others.
public sealed partial class VoiceQuotes(
    VoiceTranscript transcript,
    VoicePrefs prefs,
    PeopleFinder people,
    QuoteBook book,
    ModuleState modules,
    MusicService music,
    Notifier notifier,
    GatewayClient gateway,
    RestClient rest,
    TimeProvider time) : IHelperAware
{
    public const string Prefix = "vquote";
    public const string FixPrefix = "vquotefix";
    private static readonly TimeSpan SpokenEdit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Draft> _drafts = new();

    private sealed record Line(ulong? SpeakerId, string Name, string Text, DateTimeOffset At);

    private sealed class Draft(string token, Heard asked, HelperBot helper, List<Line> lines, List<Line> earlier, DateTimeOffset now)
    {
        public string Token => token;

        public Heard Asked => asked;

        public HelperBot Helper => helper;

        public List<Line> Lines { get; set; } = lines;

        // What was said before it, oldest first, to grow it back.
        public List<Line> Earlier => earlier;

        public (ulong Id, bool ByHelper)? Message { get; set; }

        public DateTimeOffset TouchedAt { get; set; } = now;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Done { get; set; }
    }

    private enum Edit
    {
        Earlier,
        Drop,
        Save,
        Cancel,
    }

    public Task AttachAsync(HelperBot helper)
    {
        helper.Gateway.InteractionCreate += async interaction =>
        {
            switch (interaction)
            {
                case ButtonInteraction { Data.CustomId: var id } button when id.StartsWith(Prefix + ":"):
                    await PressAsync(button);
                    break;
                case ModalInteraction { Data.CustomId: var id } modal when id.StartsWith(FixPrefix + ":"):
                    await FixAsync(modal);
                    break;
            }
        };
        return Task.CompletedTask;
    }

    public Task DetachAsync(HelperBot helper) => Task.CompletedTask;

    // Posts a draft; returns why not, or null.
    public async Task<string?> StartAsync(Heard heard, HelperBot helper, string? who)
    {
        if (!await modules.IsEnabledAsync(heard.GuildId, QuoteCommands.ModuleId))
            return $"The `{QuoteCommands.ModuleId}` module is off.";
        foreach (var stale in _drafts.Values.Where(d => time.GetUtcNow() - d.TouchedAt > Kept).ToList())
            _drafts.TryRemove(stale.Token, out _);

        var all = await prefs.AllAsync();
        bool Quotable(ulong userId) => userId == heard.UserId || all.GetValueOrDefault((heard.GuildId, userId))?.QuotesOff != true;
        var recent = transcript.Recent(heard.ChannelId).Where(s => Quotable(s.UserId)).ToList();

        var target = QuotePicker.Read(who);
        if (target is QuoteWho.Named(var name))
        {
            if ((await people.FindAsync(heard.GuildId, heard.ChannelId, name, null)).FirstOrDefault() is not { } person)
                return $"I don't know who “{name}” is.";
            if (!Quotable(person.Id))
                return $"{person.Name} would rather not be quoted.";
            target = new QuoteWho.Person(person.Id);
        }
        if (QuotePicker.Pick(recent, heard.UserId, target) is not var (start, end))
            return target is QuoteWho.Person ? "They haven't said anything I heard lately." : "There's nothing I heard lately to quote.";

        var lines = await LinesAsync(heard.GuildId, recent);
        var draft = new Draft(Guid.NewGuid().ToString("N")[..12], heard, helper, lines[start..end], lines[..start], time.GetUtcNow());
        _drafts[draft.Token] = draft;
        draft.Message = await music.PostAsync(heard.GuildId, helper, heard.ChannelId, Render(draft));
        return null;
    }

    // A spoken edit to the asker's draft, said shortly after: true when it was one.
    public async Task<bool> EditAsync(Heard heard)
    {
        if (_drafts.Values.Where(d => d.Asked.UserId == heard.UserId && d.Asked.ChannelId == heard.ChannelId && time.GetUtcNow() - d.TouchedAt < SpokenEdit)
                .MaxBy(d => d.TouchedAt) is not { } draft
            || SpokenEditOf(heard.Text) is not { } edit)
            return false;
        var message = await ApplyAsync(draft, edit);
        if (message is not null && draft.Message is { } posted)
        {
            var client = posted.ByHelper ? draft.Helper.Gateway.Rest : rest;
            try
            {
                await client.ModifyMessageAsync(heard.ChannelId, posted.Id, m => (m.Content, m.Components, m.Embeds, m.AllowedMentions) = (message.Content, message.Components, message.Embeds, AllowedMentionsProperties.None));
            }
            catch (RestException)
            {
                await music.PostAsync(heard.GuildId, draft.Helper, heard.ChannelId, message);
            }
        }
        return true;
    }

    public async Task PressAsync(ButtonInteraction button)
    {
        var parts = button.Data.CustomId.Split(':');
        if (await OwnedAsync(button, parts[1]) is not { } draft)
            return;
        if (parts[2] == "fix")
        {
            await button.SendResponseAsync(InteractionCallback.Modal(new ModalProperties($"{FixPrefix}:{draft.Token}", "Fix the quote")
            {
                new LabelProperties("Who said what, one per line", new TextInputProperties("lines", TextInputStyle.Paragraph)
                {
                    Value = string.Join('\n', draft.Lines.Select(l => $"{l.Name}: {l.Text}")),
                    MaxLength = 2000,
                }),
            }));
            return;
        }
        // Saving writes and notifies, which may take longer than Discord waits.
        await button.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        if (await ApplyAsync(draft, Enum.Parse<Edit>(parts[2], ignoreCase: true)) is { } message)
            await button.ModifyResponseAsync(m => (m.Content, m.Components, m.Embeds, m.AllowedMentions) = (message.Content, message.Components, message.Embeds, AllowedMentionsProperties.None));
    }

    public async Task FixAsync(ModalInteraction modal)
    {
        if (await OwnedAsync(modal, modal.Data.CustomId.Split(':')[1]) is not { } draft)
            return;
        var text = modal.Data.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        if (QuoteText.Parse(text) is not { } parsed)
        {
            await modal.SendResponseAsync(InteractionCallback.Message(new() { Content = $"Write one turn per line as `Name: what they said`, at most {QuoteText.MaxLines} lines.", Flags = MessageFlags.Ephemeral }));
            return;
        }
        await draft.Gate.WaitAsync();
        try
        {
            // Names as written: a member already in the draft keeps being them.
            var at = draft.Lines.FirstOrDefault()?.At ?? time.GetUtcNow();
            draft.Lines = parsed.Select(p => draft.Lines.Concat(draft.Earlier).FirstOrDefault(l => string.Equals(l.Name, p.Speaker, StringComparison.OrdinalIgnoreCase)) is { } known
                ? known with { Text = p.Text, At = at }
                : new Line(null, p.Speaker, p.Text, at)).ToList();
            draft.TouchedAt = time.GetUtcNow();
        }
        finally
        {
            draft.Gate.Release();
        }
        var message = Render(draft);
        await modal.SendResponseAsync(InteractionCallback.ModifyMessage(m => (m.Content, m.Components, m.AllowedMentions) = (message.Content, message.Components, AllowedMentionsProperties.None)));
    }

    // Only whoever asked edits the draft.
    private async Task<Draft?> OwnedAsync(Interaction interaction, string token)
    {
        string? refusal = !_drafts.TryGetValue(token, out var draft) ? "That draft has expired; ask for the quote again."
            : interaction.User.Id != draft.Asked.UserId ? $"Only <@{draft.Asked.UserId}> can change this draft."
            : null;
        if (refusal is null)
            return draft;
        await interaction.SendResponseAsync(InteractionCallback.Message(new() { Content = refusal, Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None }));
        return null;
    }

    // The draft's message after the edit, or null when it was already settled.
    private async Task<MessageProperties?> ApplyAsync(Draft draft, Edit edit)
    {
        await draft.Gate.WaitAsync();
        try
        {
            if (draft.Done)
                return null;
            draft.TouchedAt = time.GetUtcNow();
            switch (edit)
            {
                case Edit.Earlier when draft.Earlier.Count > 0 && draft.Lines.Count < QuotePicker.MostLines:
                    draft.Lines.Insert(0, draft.Earlier[^1]);
                    draft.Earlier.RemoveAt(draft.Earlier.Count - 1);
                    break;
                case Edit.Drop when draft.Lines.Count > 1:
                    draft.Earlier.Add(draft.Lines[0]);
                    draft.Lines.RemoveAt(0);
                    break;
                case Edit.Cancel:
                    draft.Done = true;
                    _drafts.TryRemove(draft.Token, out _);
                    return new() { Content = $"🎙️ <@{draft.Asked.UserId}> · No quote, then.", Components = [], Embeds = [] };
                case Edit.Save:
                    draft.Done = true;
                    _drafts.TryRemove(draft.Token, out _);
                    return await SaveAsync(draft);
            }
            return Render(draft);
        }
        finally
        {
            draft.Gate.Release();
        }
    }

    private async Task<MessageProperties> SaveAsync(Draft draft)
    {
        var asked = draft.Asked;
        var quote = await book.AddAsync(new()
        {
            GuildId = asked.GuildId,
            AddedById = asked.UserId,
            SaidAt = draft.Lines[0].At,
            Lines = draft.Lines.Select((l, i) => new QuoteLine { Position = i, SpeakerId = l.SpeakerId, SpeakerName = l.SpeakerId is null ? l.Name : null, Text = l.Text }).ToList(),
        });
        var quoted = draft.Lines.Select(l => l.SpeakerId).OfType<ulong>().Where(id => id != asked.UserId).Distinct().ToList();
        await notifier.NotifyAsync(asked.GuildId, NotificationTopics.QuotedYou, quoted,
            $"{Name(asked.GuildId, asked.UserId)} saved quote #{quote.Id} of what you said in voice. `/quote delete {quote.Id}` removes it.",
            draft.Message is { } posted ? Notifier.Link(asked.GuildId, asked.ChannelId, posted.Id) : Notifier.Link(asked.GuildId, asked.ChannelId));
        return new()
        {
            Content = $"💬 <@{asked.UserId}> saved quote #{quote.Id}",
            Embeds = [QuoteCommands.Render(quote)],
            Components = [],
        };
    }

    private static MessageProperties Render(Draft draft) => new()
    {
        Content = $"🎙️ <@{draft.Asked.UserId}> · Draft quote:\n{string.Join('\n', draft.Lines.Select(l => $"> **{l.Name}:** {l.Text}"))}\n-# Say “one more line”, “drop the first”, “save it” or “cancel”, or use the buttons.",
        Components = [new ActionRowProperties([
            new ButtonProperties($"{Prefix}:{draft.Token}:earlier", "➕ Earlier", ButtonStyle.Secondary) { Disabled = draft.Earlier.Count == 0 || draft.Lines.Count >= QuotePicker.MostLines },
            new ButtonProperties($"{Prefix}:{draft.Token}:drop", "➖ Drop first", ButtonStyle.Secondary) { Disabled = draft.Lines.Count <= 1 },
            new ButtonProperties($"{Prefix}:{draft.Token}:fix", "✏️ Fix text", ButtonStyle.Secondary),
            new ButtonProperties($"{Prefix}:{draft.Token}:save", "✅ Save", ButtonStyle.Success),
            new ButtonProperties($"{Prefix}:{draft.Token}:cancel", "✖️ Cancel", ButtonStyle.Secondary)])],
        AllowedMentions = AllowedMentionsProperties.None,
    };

    private async Task<List<Line>> LinesAsync(ulong guildId, IReadOnlyList<Said> said)
    {
        var lines = new List<Line>();
        foreach (var s in said)
            lines.Add(new(s.UserId, await NameAsync(guildId, s.UserId), s.Text, s.At));
        return lines;
    }

    private async Task<string> NameAsync(ulong guildId, ulong userId)
    {
        if (gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var cached))
            return cached.Nickname ?? cached.GlobalName ?? cached.Username;
        var member = await rest.GetGuildUserAsync(guildId, userId);
        return member.Nickname ?? member.GlobalName ?? member.Username;
    }

    private string Name(ulong guildId, ulong userId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var user) ? user.Nickname ?? user.GlobalName ?? user.Username : "Someone";

    private static Edit? SpokenEditOf(string said)
    {
        var text = NonWord().Replace(said.ToLowerInvariant().Replace("'", ""), " ").Trim();
        return text switch
        {
            _ when MoreLine().IsMatch(text) => Edit.Earlier,
            _ when DropLine().IsMatch(text) => Edit.Drop,
            _ when SaveIt().IsMatch(text) => Edit.Save,
            _ when CancelIt().IsMatch(text) => Edit.Cancel,
            _ => null,
        };
    }

    [GeneratedRegex(@"^(?:one |a )?(?:more|earlier|further back|go back|back one)(?: line| one)?(?: please)?$")]
    private static partial Regex MoreLine();

    [GeneratedRegex(@"^(?:drop|remove|cut|lose|skip)(?: the)? first(?: line| one)?(?: please)?$|^(?:shorter|less|one less)$")]
    private static partial Regex DropLine();

    [GeneratedRegex(@"^(?:save|keep|yes|yeah|perfect|thats it|good)(?: it| that| this| the quote)?(?: please)?$")]
    private static partial Regex SaveIt();

    [GeneratedRegex(@"^(?:cancel|no|nope|never mind|nevermind|forget it|scrap it)(?: it| that)?$")]
    private static partial Regex CancelIt();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();
}

// Buttons and fixes on drafts the main bot posted for a helper; a helper's own reach VoiceQuotes directly.
public sealed class VoiceQuoteButtons(VoiceQuotes quotes) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction(VoiceQuotes.Prefix)]
    public Task EditAsync(string token, string edit) => quotes.PressAsync(Context.Interaction);
}

public sealed class VoiceQuoteFixes(VoiceQuotes quotes) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction(VoiceQuotes.FixPrefix)]
    public Task FixAsync(string token) => quotes.FixAsync(Context.Interaction);
}
