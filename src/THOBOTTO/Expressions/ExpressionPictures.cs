using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Expressions;

// Pictures the bot posts for members: a library sticker (Discord's list holds only so many, and webhooks can't send
// stickers), or an emoji made big.
public sealed partial class ExpressionPictures(ExpressionShelf shelf, EmojiFinder finder, [FromKeyedServices(nameof(ExpressionPictures))] HttpClient http)
{
    public const int MostAtOnce = 3;
    private const int BigSize = 256;

    public async Task<(LibrarySticker Sticker, string FileName, byte[] Bytes)?> StickerAsync(ulong guildId, string name)
    {
        var stickers = await shelf.StickersAsync(guildId);
        var spaced = name.Replace('_', ' ');
        if ((stickers.FirstOrDefault(s => s.Name == name) ?? stickers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(spaced, StringComparison.OrdinalIgnoreCase))) is not { } sticker
            || await shelf.FindAsync(guildId, sticker.Id) is not { } found)
            return null;
        return (sticker, $"{Unfit().Replace(sticker.Name, "_")}.{(sticker.ContentType == "image/gif" ? "gif" : "png")}", found.Image);
    }

    public async Task<(UsableEmoji Emoji, string FileName, byte[] Bytes)?> BigAsync(Guild guild, GuildUser member, string name)
    {
        if (EmojiFinder.Named(await finder.UsableAsync(guild, member), name) is not { } emoji)
            return null;
        return await EmojiAsync(emoji.Id, emoji.Animated, BigSize) is { } bytes ? (emoji, $"{emoji.Name}.{(emoji.Animated ? "gif" : "png")}", bytes) : null;
    }

    // Any custom emoji's picture from Discord's CDN, at most that size.
    public async Task<byte[]?> EmojiAsync(ulong id, bool animated, int size)
    {
        try
        {
            return await http.GetByteArrayAsync($"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}?size={size}");
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // sticker:name and big:name in a message: a word, _ and - read as spaces.
    public static Regex Word(string prefix) => prefix == "sticker" ? StickerWord() : BigWord();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_/:])sticker:([\p{L}\p{N}_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StickerWord();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_/:])big:([A-Za-z0-9_]{2,32})", RegexOptions.IgnoreCase)]
    private static partial Regex BigWord();

    [GeneratedRegex(@"[^\w\-]+")]
    private static partial Regex Unfit();
}

// sticker:name in a message: that library sticker's picture posted with it, under the member's name.
public sealed class StickerWords(ExpressionPictures pictures, ExpressionShelf shelf, SettingsStore settings, ModuleState modules) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Words;

    public bool Wants(string text, ulong channelId, ulong userId) => ExpressionPictures.Word("sticker").IsMatch(text);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        // An edit can't add a picture.
        if (repost.Message is null || !preference.EmojiNames || !await modules.IsEnabledAsync(guildId, ExpressionShelf.ModuleId)
            || !(await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId)).StickersByName)
            return;
        var found = new Dictionary<string, (LibrarySticker Sticker, string FileName, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ExpressionPictures.Word("sticker").Matches(repost.Text))
        {
            if (found.Count < ExpressionPictures.MostAtOnce && !found.ContainsKey(m.Groups[1].Value) && await pictures.StickerAsync(guildId, m.Groups[1].Value) is { } sticker)
                found[m.Groups[1].Value] = sticker;
        }
        if (found.Count == 0)
            return;
        repost.Text = ExpressionPictures.Word("sticker").Replace(repost.Text, m => found.ContainsKey(m.Groups[1].Value) ? "" : m.Value).Trim();
        repost.Files.AddRange(found.Values.Select(f => (f.FileName, f.Bytes)));
        repost.Changed = true;
        var userId = repost.Member.Id;
        repost.Then.Add(() =>
        {
            foreach (var (sticker, _, _) in found.Values)
                shelf.Used(guildId, userId, sticker.Id, sticker.CreatorId);
            return Task.CompletedTask;
        });
    }
}

// big:name in a message: that emoji as a large picture posted with it, under the member's name.
public sealed class BigWords(ExpressionPictures pictures, ExpressionShelf shelf, SettingsStore settings, ModuleState modules) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Words;

    public bool Wants(string text, ulong channelId, ulong userId) => ExpressionPictures.Word("big").IsMatch(text);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        if (repost.Message is null || !preference.EmojiNames || !await modules.IsEnabledAsync(guildId, ExpressionShelf.ModuleId)
            || !(await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId)).BigEmoji)
            return;
        var found = new Dictionary<string, (UsableEmoji Emoji, string FileName, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ExpressionPictures.Word("big").Matches(repost.Text))
        {
            if (found.Count < ExpressionPictures.MostAtOnce && !found.ContainsKey(m.Groups[1].Value) && await pictures.BigAsync(repost.Guild, repost.Member, m.Groups[1].Value) is { } big)
                found[m.Groups[1].Value] = big;
        }
        if (found.Count == 0)
            return;
        repost.Text = ExpressionPictures.Word("big").Replace(repost.Text, m => found.ContainsKey(m.Groups[1].Value) ? "" : m.Value).Trim();
        repost.Files.AddRange(found.Values.Select(f => (f.FileName, f.Bytes)));
        repost.Changed = true;
        var userId = repost.Member.Id;
        repost.Then.Add(() =>
        {
            foreach (var big in found.Values)
                shelf.Used(guildId, userId, big.Emoji.Id);
            return Task.CompletedTask;
        });
    }
}

// /sticker send and /emoji big: a picture posted under the member's name (as the bot's reply where that can't be).
public static class PicturePost
{
    public static async Task SendAsync(ApplicationCommandModule<ApplicationCommandContext> module, AsMember asMember, ModuleState modules, SettingsStore settings,
        string? text, string fileName, byte[] bytes, Func<Task> used)
    {
        var interaction = module.Context.Interaction;
        var content = text?.Trim() ?? "";
        if (!AsMember.Can(interaction) || !await modules.IsEnabledAsync(interaction.Guild!.Id, Reposter.ModuleId))
        {
            await module.RespondAsync(InteractionCallback.Message(new() { Content = content, Attachments = [new AttachmentProperties(fileName, new MemoryStream(bytes))], AllowedMentions = AllowedMentionsProperties.None }));
            await used();
            return;
        }
        await module.RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var withUsername = (await settings.GetAsync<RepostRules>(interaction.Guild.Id, Reposter.ModuleId)).ShowUsername;
        var (_, problem) = await asMember.PostAsync((TextGuildChannel)interaction.Channel, (GuildInteractionUser)interaction.User, withUsername,
            new() { Content = content, Attachments = [new AttachmentProperties(fileName, new MemoryStream(bytes))] });
        if (problem is not null)
            await module.ModifyResponseAsync(m => m.Content = $"It couldn't be posted ({problem}).");
        else
        {
            await module.DeleteResponseAsync();
            await used();
        }
    }
}

public sealed class LibraryStickerAutocomplete(ExpressionShelf shelf) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
        => context.Interaction.GuildId is not { } guildId ? []
            : (await shelf.StickersAsync(guildId)).Where(s => s.Name.Contains(option.Value ?? "", StringComparison.OrdinalIgnoreCase)).Take(25)
                .Select(s => new ApplicationCommandOptionChoiceProperties(s.Name, s.Name));
}

public sealed class UsableEmojiAutocomplete(EmojiFinder finder, GatewayClient gateway) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
        => context.Interaction.GuildId is not { } guildId || !gateway.Cache.Guilds.TryGetValue(guildId, out var guild) || context.User is not GuildUser member ? []
            : (await finder.UsableAsync(guild, member)).Where(e => e.Name.Contains(option.Value ?? "", StringComparison.OrdinalIgnoreCase)).DistinctBy(e => e.Name).Take(25)
                .Select(e => new ApplicationCommandOptionChoiceProperties(e.Name, e.Name));
}
