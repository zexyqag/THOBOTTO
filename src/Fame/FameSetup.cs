using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Fame;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("fame", "The hall of fame (needs fame.manage)")]
    [RequirePermission(BotPermissions.ManageFame)]
    public sealed partial class FameSetup(SettingsStore settings) : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Interaction.GuildId!.Value;

        [SubSlashCommand("general", "Showcase channel, threshold, bonus, age limit")]
        public Task<InteractionMessageProperties> GeneralAsync(
            [SlashCommandParameter(Description = "Where famous messages are reposted", AllowedChannelTypes = [ChannelType.TextGuildChannel])] Channel? showcase = null,
            [SlashCommandParameter(Description = "Different people who must react", MinValue = 1, MaxValue = 1000)] int? threshold = null,
            [SlashCommandParameter(Description = "Points for the author", MinValue = 0, MaxValue = 1_000_000)] double? bonus = null,
            [SlashCommandParameter(Name = "max-age-days", Description = "Reactions on older messages don't count", MinValue = 1, MaxValue = 365)] int? maxAgeDays = null)
            => UpdateAsync(r => r with
            {
                ShowcaseChannelId = showcase?.Id ?? r.ShowcaseChannelId,
                Threshold = threshold ?? r.Threshold,
                Bonus = bonus ?? r.Bonus,
                MaxAgeDays = maxAgeDays ?? r.MaxAgeDays,
            });

        [SubSlashCommand("exclude", "Stop a channel's messages from counting")]
        public Task<InteractionMessageProperties> ExcludeAsync([SlashCommandParameter(Description = "Channel")] Channel channel)
            => UpdateAsync(r => r with { ExcludedChannelIds = r.ExcludedChannelIds.Contains(channel.Id) ? r.ExcludedChannelIds : [.. r.ExcludedChannelIds, channel.Id] });

        [SubSlashCommand("include", "Let an excluded channel count again")]
        public Task<InteractionMessageProperties> IncludeAsync([SlashCommandParameter(Description = "Channel")] Channel channel)
            => UpdateAsync(r => r with { ExcludedChannelIds = [.. r.ExcludedChannelIds.Where(id => id != channel.Id)] });

        [SubSlashCommand("emojis", "Which emojis count")]
        public Task<InteractionMessageProperties> EmojisAsync(
            [SlashCommandParameter(Description = "Emojis separated by spaces, or \"any\"", MaxLength = 1000)] string emojis)
        {
            var list = emojis.Trim().Equals("any", StringComparison.OrdinalIgnoreCase)
                ? []
                : emojis.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => CustomEmoji().Match(e) is { Success: true } m ? m.Groups[1].Value : e)
                    .Distinct()
                    .ToList();
            return UpdateAsync(r => r with { Emojis = list });
        }

        private async Task<InteractionMessageProperties> UpdateAsync(Func<FameRules, FameRules> change)
        {
            var before = await settings.GetAsync<FameRules>(GuildId, HallOfFame.ModuleId);
            var after = change(before);
            var changed = !SettingsStore.Same(before, after);
            if (changed)
                await settings.SetAsync(GuildId, HallOfFame.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

            var emojis = after.Emojis.Count == 0 ? "any emoji" : string.Join(' ', after.Emojis.Select(e => ulong.TryParse(e, out var id) ? $"<:e:{id}>" : e));
            var excluded = after.ExcludedChannelIds.Count == 0 ? "none" : string.Join(' ', after.ExcludedChannelIds.Select(id => $"<#{id}>"));
            return Replies.Ephemeral($"""
                {(changed ? "Updated." : "Nothing changed.")}
                Showcase: {(after.ShowcaseChannelId is { } c ? $"<#{c}>" : "not set, so nothing is tracked")}
                {after.Threshold} different {(after.Threshold == 1 ? "person" : "people")} must react, with {emojis}, within {after.MaxAgeDays} days of posting.
                Bonus for the author: {after.Bonus} (only while the `points` module is on).
                Excluded channels: {excluded}
                """);
        }

        // <:name:id> or <a:name:id>
        [GeneratedRegex(@"^<a?:\w+:(\d+)>$")]
        private static partial Regex CustomEmoji();
    }
}
