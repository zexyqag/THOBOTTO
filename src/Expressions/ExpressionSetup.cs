using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Expressions;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("emojis", "Member-made emojis and stickers: votes, costs, royalties, retirement (needs emojis.manage)")]
    [RequirePermission(BotPermissions.ManageExpressions)]
    public async Task<InteractionMessageProperties> EmojisAsync(
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
        var before = await Get<SettingsStore>().GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId);
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
            await Get<SettingsStore>().SetAsync(guildId, ExpressionShelf.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Vote channel: {(after.VoteChannelId is { } c ? $"<#{c}>" : "not set")}
            Proposing costs {after.ProposeCost} (refunded if accepted); accepted ones pay the creator {after.AcceptBonus}.
            Accepted at {after.VoteMargin} more 👍 than 👎 within {after.VoteDays} days.
            Royalties: {after.RoyaltyPerUse} per use by others, at most {after.RoyaltyDailyCap} a day.
            Retired after {after.RetireAfterDays} days unused. Full slots: {(after.ReplaceLeastUsed ? "replace the least used" : "wait")}.
            """);
    }
}
