using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Panel;
using THOBOTTO.Reposts;

namespace THOBOTTO.Expressions;

public static class OtherServers
{
    public const string None = "none";
    public const string Shared = "shared";
    public const string All = "all";
}

// The custom emoji a member can have the bot use for them in a server, by name: the server's own (by its roles), then,
// as its settings allow, those of other servers the bot is in.
public sealed partial class EmojiFinder(GatewayClient gateway, PanelAccess members, SettingsStore settings)
{
    public async Task<IReadOnlyList<CustomEmoji>> UsableAsync(Guild guild, GuildUser member)
    {
        var own = guild.Emojis.Values.Where(e => e.Available != false && (e.AllowedRoles is not { Count: > 0 } roles || roles.Any(member.RoleIds.Contains)));
        IEnumerable<Guild> others = (await settings.GetAsync<ExpressionRules>(guild.Id, ExpressionShelf.ModuleId)).EmojiFrom switch
        {
            OtherServers.Shared => await members.GuildsAsync(member.Id),
            OtherServers.All => gateway.Cache.Guilds.Values.OrderBy(g => g.Name),
            _ => [],
        };
        return [.. own, .. others.Where(g => g.Id != guild.Id).SelectMany(g => g.Emojis.Values.Where(e => e.Available != false && e.AllowedRoles is not { Count: > 0 }))];
    }

    // Its exact name first, then any case.
    public static CustomEmoji? Named(IReadOnlyList<CustomEmoji> emojis, string name)
        => emojis.FirstOrDefault(e => e.Name == name) ?? emojis.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // :name: as Discord's apps leave it when they can't use that emoji; not one already made (<:name:id>), nor a time.
    public static Regex Names() => NamePattern();

    [GeneratedRegex(@"(?<![<\w:]):([A-Za-z0-9_]{2,32}):(?![\d:])")]
    private static partial Regex NamePattern();
}

// :name: in a message for an emoji the member couldn't use there (an animated one without Nitro, one from another
// server the bot is in): posted again under their name with the emoji in it.
public sealed class EmojiNames(EmojiFinder finder, ExpressionShelf shelf, SettingsStore settings, ModuleState modules) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Words;

    public bool Wants(string text, ulong channelId, ulong userId) => EmojiFinder.Names().IsMatch(text);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        if (!preference.EmojiNames || !await modules.IsEnabledAsync(guildId, ExpressionShelf.ModuleId) || !(await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId)).ByName)
            return;
        var usable = await finder.UsableAsync(repost.Guild, repost.Member);
        var used = new List<CustomEmoji>();
        var text = RepostText.OutsideCode(repost.Text, part => EmojiFinder.Names().Replace(part, m =>
        {
            if (EmojiFinder.Named(usable, m.Groups[1].Value) is not { } emoji)
                return m.Value;
            used.Add(emoji);
            return emoji.ToString()!;
        }));
        if (used.Count == 0)
            return;
        (repost.Text, repost.Changed) = (text, true);
        var userId = repost.Member.Id;
        repost.Then.Add(() =>
        {
            foreach (var emoji in used.DistinctBy(e => e.Id))
                shelf.Used(guildId, userId, emoji.Id);
            return Task.CompletedTask;
        });
    }
}

// The bot reacts with an emoji a member asked for, so they can press it to add theirs; once they have, or after a
// while, the bot's own reaction goes.
public sealed class ReactThrough(RestClient rest, TimeProvider time, ILogger<ReactThrough> logger)
{
    private static readonly TimeSpan Waits = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<(ulong MessageId, ulong EmojiId), (ulong ChannelId, string Name, ulong UserId)> _waiting = new();

    // Null once reacted, else why not.
    public async Task<string?> ReactAsync(ulong channelId, ulong messageId, CustomEmoji emoji, ulong userId)
    {
        try
        {
            await rest.AddMessageReactionAsync(channelId, messageId, new(emoji.Name, emoji.Id));
        }
        catch (RestException ex)
        {
            return ex.Message;
        }
        _waiting[(messageId, emoji.Id)] = (channelId, emoji.Name, userId);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Waits, time);
            await TakeBackAsync(messageId, emoji.Id);
        });
        return null;
    }

    public async Task OnReactionAsync(ulong messageId, ulong? emojiId, ulong userId)
    {
        if (emojiId is { } id && _waiting.TryGetValue((messageId, id), out var waiting) && waiting.UserId == userId)
            await TakeBackAsync(messageId, id);
    }

    private async Task TakeBackAsync(ulong messageId, ulong emojiId)
    {
        if (!_waiting.TryRemove((messageId, emojiId), out var waiting))
            return;
        try
        {
            await rest.DeleteCurrentUserMessageReactionAsync(waiting.ChannelId, messageId, new(waiting.Name, emojiId));
        }
        catch (RestException ex)
        {
            logger.LogInformation("Taking back the reaction on {MessageId} failed: {Problem}", messageId, ex.Message);
        }
    }
}

public sealed class ReactThroughHandler(ReactThrough reactions) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg) => await reactions.OnReactionAsync(arg.MessageId, arg.Emoji.Id, arg.UserId);
}

// Right-click a message, Apps, React with emoji: any emoji the member could use through the bot, by name.
public sealed class ReactCommand(SettingsStore settings, ModuleState modules) : GuildCommandModule
{
    [MessageCommand("React with emoji", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> ReactAsync(RestMessage message)
    {
        if (!await modules.IsEnabledAsync(GuildId, ExpressionShelf.ModuleId))
            return InteractionCallback.Message(Replies.Ephemeral(ModuleRegistry.Off(ExpressionShelf.ModuleId)));
        if (!(await settings.GetAsync<ExpressionRules>(GuildId, ExpressionShelf.ModuleId)).ReactThroughBot)
            return InteractionCallback.Message(Replies.Ephemeral("This server doesn't react for members."));
        return InteractionCallback.Modal(new ModalProperties($"reactwith:{message.ChannelId}:{message.Id}", "React with emoji")
        {
            new LabelProperties("Its name", new TextInputProperties("name", TextInputStyle.Short) { Placeholder = "partyparrot", MaxLength = 34 }),
        });
    }
}

public sealed class ReactModal(EmojiFinder finder, ReactThrough reactions) : GuildModalModule
{
    [ComponentInteraction("reactwith")]
    public async Task<InteractionCallbackProperties> ReactAsync(ulong channelId, ulong messageId)
    {
        var name = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value.Trim().Trim(':');
        var usable = await finder.UsableAsync(Guild, (GuildUser)Context.User);
        if (EmojiFinder.Named(usable, name) is not { } emoji)
        {
            var close = usable.Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).Distinct().Take(8).ToList();
            return InteractionCallback.Message(Replies.Ephemeral(close.Count == 0 ? $"No emoji named {name} here." : $"No emoji named {name}; close: {string.Join(", ", close)}."));
        }
        return InteractionCallback.Message(Replies.Ephemeral(await reactions.ReactAsync(channelId, messageId, emoji, Context.User.Id) is { } problem
            ? $"It couldn't react ({problem})."
            : $"Reacted with {emoji}: press it to add yours, and the bot's goes."));
    }
}
