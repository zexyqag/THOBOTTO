using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Modules;

namespace THOBOTTO.Points;

public sealed partial class PointsCommands
{
    [SubSlashCommand("settings", "How points are earned (needs points.manage)")]
    [RequirePermission(BotPermissions.ManagePoints)]
    public sealed class SettingsCommands(PointsEngine points) : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Interaction.GuildId!.Value;

        [SubSlashCommand("general", "Currency, base rate, idle floor, expiry, leaderboard, kudos")]
        public Task<InteractionMessageProperties> GeneralAsync(
            [SlashCommandParameter(Name = "currency-name", MaxLength = 32)] string? currencyName = null,
            [SlashCommandParameter(Name = "currency-emoji", Description = "Emoji shown with the currency; \"none\" removes it", MaxLength = 64)] string? currencyEmoji = null,
            [SlashCommandParameter(Name = "base-per-minute", Description = "Points per minute at activity level 1", MinValue = 0, MaxValue = 100)] double? basePerMinute = null,
            [SlashCommandParameter(Name = "idle-floor", Description = "Activity level when idle; negative drains", MinValue = -1, MaxValue = 0)] double? idleFloor = null,
            [SlashCommandParameter(Name = "allow-negative", Description = "Whether balances may go below zero")] bool? allowNegative = null,
            [SlashCommandParameter(Name = "leaderboard-public", Description = "Whether /points top is visible to everyone")] bool? leaderboardPublic = null,
            [SlashCommandParameter(Name = "weekly-expiry-percent", Description = "Share of a balance lost per week; 0 is off", MinValue = 0, MaxValue = 100)] double? weeklyExpiry = null,
            [SlashCommandParameter(Name = "kudos-daily-limit", Description = "Most points one member can give away per 24 hours", MinValue = 0, MaxValue = 1_000_000)] double? kudosDailyLimit = null)
            => UpdateAsync(r => r with
            {
                CurrencyName = currencyName ?? r.CurrencyName,
                CurrencyEmoji = currencyEmoji is null ? r.CurrencyEmoji : currencyEmoji == "none" ? null : currencyEmoji,
                BasePerMinute = basePerMinute ?? r.BasePerMinute,
                IdleFloor = idleFloor ?? r.IdleFloor,
                AllowNegativeBalance = allowNegative ?? r.AllowNegativeBalance,
                LeaderboardPublic = leaderboardPublic ?? r.LeaderboardPublic,
                WeeklyExpiryPercent = weeklyExpiry ?? r.WeeklyExpiryPercent,
                KudosDailyLimit = kudosDailyLimit ?? r.KudosDailyLimit,
            });

        [SubSlashCommand("voice", "How voice raises the activity level")]
        public Task<InteractionMessageProperties> VoiceAsync(
            [SlashCommandParameter(Description = "Level reached after a while in voice", MinValue = 0, MaxValue = 10)] double? max = null,
            [SlashCommandParameter(Name = "rise-minutes", Description = "Time constant going up (~95% after three)", MinValue = 1, MaxValue = 1440)] double? rise = null,
            [SlashCommandParameter(Name = "fall-minutes", Description = "Time constant going down after leaving", MinValue = 1, MaxValue = 1440)] double? fall = null)
            => UpdateAsync(r => r with
            {
                VoiceMax = max ?? r.VoiceMax,
                VoiceRiseMinutes = rise ?? r.VoiceRiseMinutes,
                VoiceFallMinutes = fall ?? r.VoiceFallMinutes,
            });

        [SubSlashCommand("chat", "How messages raise the activity level")]
        public Task<InteractionMessageProperties> ChatAsync(
            [SlashCommandParameter(Description = "Highest chat level", MinValue = 0, MaxValue = 10)] double? max = null,
            [SlashCommandParameter(Description = "Bump per message", MinValue = 0, MaxValue = 10)] double? bump = null,
            [SlashCommandParameter(Name = "per-char", Description = "Extra bump per character", MinValue = 0, MaxValue = 1)] double? perChar = null,
            [SlashCommandParameter(Name = "bump-max", Description = "Largest bump from one message", MinValue = 0, MaxValue = 10)] double? bumpMax = null,
            [SlashCommandParameter(Name = "cooldown-seconds", Description = "Only one message counts per this many seconds", MinValue = 0, MaxValue = 3600)] int? cooldown = null,
            [SlashCommandParameter(Name = "half-life-minutes", Description = "How fast the chat level fades", MinValue = 1, MaxValue = 1440)] double? halfLife = null)
            => UpdateAsync(r => r with
            {
                ChatMax = max ?? r.ChatMax,
                ChatBump = bump ?? r.ChatBump,
                ChatBumpPerChar = perChar ?? r.ChatBumpPerChar,
                ChatBumpMax = bumpMax ?? r.ChatBumpMax,
                ChatCooldownSeconds = cooldown ?? r.ChatCooldownSeconds,
                ChatHalfLifeMinutes = halfLife ?? r.ChatHalfLifeMinutes,
            });

        [SubSlashCommand("reactions", "How reactions raise the activity level")]
        public Task<InteractionMessageProperties> ReactionsAsync(
            [SlashCommandParameter(Name = "received-max", MinValue = 0, MaxValue = 10)] double? receivedMax = null,
            [SlashCommandParameter(Name = "received-bump", MinValue = 0, MaxValue = 10)] double? receivedBump = null,
            [SlashCommandParameter(Name = "per-reactor", Description = "Reactions counted from one person per window", MinValue = 0, MaxValue = 100)] int? perReactor = null,
            [SlashCommandParameter(Name = "window-minutes", MinValue = 1, MaxValue = 1440)] int? window = null,
            [SlashCommandParameter(Name = "received-half-life", Description = "Minutes", MinValue = 1, MaxValue = 1440)] double? receivedHalfLife = null,
            [SlashCommandParameter(Name = "given-max", MinValue = 0, MaxValue = 10)] double? givenMax = null,
            [SlashCommandParameter(Name = "given-bump", MinValue = 0, MaxValue = 10)] double? givenBump = null,
            [SlashCommandParameter(Name = "given-half-life", Description = "Minutes", MinValue = 1, MaxValue = 1440)] double? givenHalfLife = null)
            => UpdateAsync(r => r with
            {
                ReceivedMax = receivedMax ?? r.ReceivedMax,
                ReceivedBump = receivedBump ?? r.ReceivedBump,
                ReceivedPerReactor = perReactor ?? r.ReceivedPerReactor,
                ReceivedWindowMinutes = window ?? r.ReceivedWindowMinutes,
                ReceivedHalfLifeMinutes = receivedHalfLife ?? r.ReceivedHalfLifeMinutes,
                GivenMax = givenMax ?? r.GivenMax,
                GivenBump = givenBump ?? r.GivenBump,
                GivenHalfLifeMinutes = givenHalfLife ?? r.GivenHalfLifeMinutes,
            });

        private async Task<InteractionMessageProperties> UpdateAsync(Func<PointRules, PointRules> change)
        {
            var before = points.Rules(GuildId);
            var after = change(before);
            var changed = after != before;
            if (changed)
                await points.SetRulesAsync(GuildId, after, Context.User.Id, SettingsStore.Diff(before, after));

            return Replies.Ephemeral($"{(changed ? "Updated." : "Nothing changed.")}\n```\n{Describe(after)}\n```");
        }

        private static string Describe(PointRules r) => $"""
            currency          {r.CurrencyName} {r.CurrencyEmoji}
            base per minute   {r.BasePerMinute}
            idle floor        {r.IdleFloor}
            allow negative    {r.AllowNegativeBalance}
            leaderboard       {(r.LeaderboardPublic ? "public" : "private")}
            weekly expiry     {r.WeeklyExpiryPercent}%
            kudos per day     {r.KudosDailyLimit}
            voice             max {r.VoiceMax}, rise {r.VoiceRiseMinutes} min, fall {r.VoiceFallMinutes} min
            chat              max {r.ChatMax}, bump {r.ChatBump} + {r.ChatBumpPerChar}/char (≤ {r.ChatBumpMax}), cooldown {r.ChatCooldownSeconds} s, half-life {r.ChatHalfLifeMinutes} min
            received          max {r.ReceivedMax}, bump {r.ReceivedBump}, {r.ReceivedPerReactor} per reactor per {r.ReceivedWindowMinutes} min, half-life {r.ReceivedHalfLifeMinutes} min
            given             max {r.GivenMax}, bump {r.GivenBump}, half-life {r.GivenHalfLifeMinutes} min
            """;
    }
}
