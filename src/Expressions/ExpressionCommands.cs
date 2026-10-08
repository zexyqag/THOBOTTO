using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Modules;

namespace THOBOTTO.Expressions;

[SlashCommand("emoji", "Member-made emojis", Contexts = [InteractionContextType.Guild])]
public sealed class EmojiCommands(ExpressionShelf shelf, ModuleState modules, SettingsStore settings) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("propose", "Put an emoji up for a vote")]
    public Task ProposeAsync(
        [SlashCommandParameter(Description = "Name, e.g. thobotto_cry (letters, digits, _)", MinLength = 2, MaxLength = 32)] string name,
        [SlashCommandParameter(Description = "PNG, JPEG, GIF or WebP, at most 256 KB")] Attachment image)
        => DeferredAsync(this, modules, () => shelf.ProposeAsync(Context.Guild!.Id, ExpressionKinds.Emoji, name, null, Context.User.Id, image));

    [SubSlashCommand("revive", "Put a retired emoji up for a vote again")]
    public Task ReviveAsync([SlashCommandParameter(Description = "Its name")] string name)
        => DeferredAsync(this, modules, () => shelf.ReviveAsync(Context.Guild!.Id, ExpressionKinds.Emoji, name, Context.User.Id));

    [SubSlashCommand("list", "Member-made emojis and stickers, with how much they're used")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        if (await ModuleOffAsync(modules, Context) is { } off)
            return off;

        var all = await shelf.ListAsync(Context.Guild!.Id);
        if (all.Count == 0)
            return Replies.Ephemeral("None yet. Propose one with `/emoji propose` or `/sticker propose`.");

        var lines = all.GroupBy(e => e.State).Select(g => $"**{g.Key}**\n" + string.Join('\n', g.Select(e =>
            $"{(e.Kind == ExpressionKinds.Emoji ? (e.DiscordId is { } id ? $"<{(e.Animated ? "a" : "")}:{e.Name}:{id}>" : $":{e.Name}:") : $"sticker {e.Name}")} by <@{e.CreatorId}>"
            + (e.State == ExpressionStates.Live ? $", used {e.Uses}×" : ""))));
        return Replies.Ephemeral(string.Join("\n\n", lines));
    }

    [SubSlashCommand("settings", "Votes, costs, royalties, retirement (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public async Task<InteractionMessageProperties> SettingsAsync(
        [SlashCommandParameter(Name = "vote-channel", Description = "Where proposals are voted on", AllowedChannelTypes = [ChannelType.TextGuildChannel])] Channel? voteChannel = null,
        [SlashCommandParameter(Name = "propose-cost", Description = "Refunded if accepted", MinValue = 0, MaxValue = 1_000_000)] double? proposeCost = null,
        [SlashCommandParameter(Name = "accept-bonus", Description = "For the creator when accepted", MinValue = 0, MaxValue = 1_000_000)] double? acceptBonus = null,
        [SlashCommandParameter(Name = "vote-margin", Description = "More 👍 than 👎 needed", MinValue = 1, MaxValue = 1000)] int? voteMargin = null,
        [SlashCommandParameter(Name = "vote-days", Description = "How long a vote stays open", MinValue = 1, MaxValue = 60)] int? voteDays = null,
        [SlashCommandParameter(Name = "royalty-per-use", Description = "For the creator per use by someone else", MinValue = 0, MaxValue = 1000)] double? royalty = null,
        [SlashCommandParameter(Name = "royalty-daily-cap", MinValue = 0, MaxValue = 1_000_000)] double? royaltyCap = null,
        [SlashCommandParameter(Name = "retire-after-days", Description = "Unused this long and it's retired", MinValue = 1, MaxValue = 3650)] int? retireDays = null,
        [SlashCommandParameter(Name = "replace-least-used", Description = "When slots are full: retire the least used instead of waiting")] bool? replace = null)
    {
        var guildId = Context.Guild!.Id;
        var before = await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId);
        var after = before with
        {
            VoteChannelId = voteChannel?.Id ?? before.VoteChannelId,
            ProposeCost = proposeCost ?? before.ProposeCost,
            AcceptBonus = acceptBonus ?? before.AcceptBonus,
            VoteMargin = voteMargin ?? before.VoteMargin,
            VoteDays = voteDays ?? before.VoteDays,
            RoyaltyPerUse = royalty ?? before.RoyaltyPerUse,
            RoyaltyDailyCap = royaltyCap ?? before.RoyaltyDailyCap,
            RetireAfterDays = retireDays ?? before.RetireAfterDays,
            ReplaceLeastUsed = replace ?? before.ReplaceLeastUsed,
        };

        var changed = after != before;
        if (changed)
            await settings.SetAsync(guildId, ExpressionShelf.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Vote channel: {(after.VoteChannelId is { } c ? $"<#{c}>" : "not set")}
            Proposing costs {after.ProposeCost} (refunded if accepted); accepted ones pay the creator {after.AcceptBonus}.
            Accepted at {after.VoteMargin} more 👍 than 👎 within {after.VoteDays} days.
            Royalties: {after.RoyaltyPerUse} per use by others, at most {after.RoyaltyDailyCap} a day.
            Retired after {after.RetireAfterDays} days unused. Full slots: {(after.ReplaceLeastUsed ? "replace the least used" : "wait")}.
            """);
    }

    // Proposals download the image and post the vote, which can outlast Discord's wait for a reply.
    internal static async Task DeferredAsync(ApplicationCommandModule<ApplicationCommandContext> module, ModuleState modules, Func<Task<string>> work)
    {
        if (await ModuleOffAsync(modules, module.Context) is { } off)
        {
            await module.RespondAsync(InteractionCallback.Message(off));
            return;
        }

        await module.RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await work();
        await module.ModifyResponseAsync(m => m.Content = result);
    }

    internal static async Task<InteractionMessageProperties?> ModuleOffAsync(ModuleState modules, ApplicationCommandContext context)
        => await modules.IsEnabledAsync(context.Guild!.Id, ExpressionShelf.ModuleId) ? null : Replies.Ephemeral($"The `{ExpressionShelf.ModuleId}` module is off.");
}

[SlashCommand("sticker", "Member-made stickers", Contexts = [InteractionContextType.Guild])]
public sealed class StickerCommands(ExpressionShelf shelf, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("propose", "Put a sticker up for a vote")]
    public Task ProposeAsync(
        [SlashCommandParameter(Description = "Name", MinLength = 2, MaxLength = 30)] string name,
        [SlashCommandParameter(Description = "PNG, APNG or GIF, at most 512 KB (320×320 works best)")] Attachment image,
        [SlashCommandParameter(Description = "The emoji it's suggested for, e.g. 😂", MaxLength = 50)] string tag = "⭐")
        => EmojiCommands.DeferredAsync(this, modules, () => shelf.ProposeAsync(Context.Guild!.Id, ExpressionKinds.Sticker, name.Trim(), tag, Context.User.Id, image));

    [SubSlashCommand("revive", "Put a retired sticker up for a vote again")]
    public Task ReviveAsync([SlashCommandParameter(Description = "Its name")] string name)
        => EmojiCommands.DeferredAsync(this, modules, () => shelf.ReviveAsync(Context.Guild!.Id, ExpressionKinds.Sticker, name.Trim(), Context.User.Id));
}

public sealed class ExpressionVoteButtons(ExpressionShelf shelf) : ComponentInteractionModule<ButtonInteractionContext>
{
    // Deferred: an accepting vote uploads the emoji, which can take longer than Discord waits for a reply.
    [ComponentInteraction("exvote")]
    public async Task VoteAsync(long id, int up)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await shelf.VoteAsync(id, Context.User.Id, up == 1);
        await ModifyResponseAsync(m => m.Content = result);
    }
}
