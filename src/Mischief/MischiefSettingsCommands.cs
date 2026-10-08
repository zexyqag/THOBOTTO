using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Modules;

namespace THOBOTTO.Mischief;

[SlashCommand("mischief", "Mischief settings", Contexts = [InteractionContextType.Guild])]
public sealed class MischiefSettingsCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("settings", "Prices and cooldowns (needs mischief.manage)")]
    [RequirePermission(BotPermissions.ManageMischief)]
    public sealed class SettingsCommands(SettingsStore settings) : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Interaction.GuildId!.Value;

        [SubSlashCommand("rename", "What /rename costs")]
        public Task<InteractionMessageProperties> RenameAsync(
            [SlashCommandParameter(Description = "Price of the first rename in the window", MinValue = 0, MaxValue = 1_000_000)] double? cost = null,
            [SlashCommandParameter(Description = "Price multiplier per recent rename of the same person", MinValue = 1, MaxValue = 10)] double? growth = null,
            [SlashCommandParameter(Name = "window-hours", Description = "How far back renames count towards the price", MinValue = 0, MaxValue = 720)] double? windowHours = null,
            [SlashCommandParameter(Name = "cooldown-minutes", Description = "Minimum time between renames of the same person", MinValue = 0, MaxValue = 10080)] int? cooldown = null,
            [SlashCommandParameter(Name = "self-multiplier", Description = "Renaming yourself costs the base price × this", MinValue = 1, MaxValue = 1000)] double? selfMultiplier = null)
            => UpdateAsync(r => r with
            {
                RenameCost = cost ?? r.RenameCost,
                RenameGrowth = growth ?? r.RenameGrowth,
                RenameWindowHours = windowHours ?? r.RenameWindowHours,
                RenameCooldownMinutes = cooldown ?? r.RenameCooldownMinutes,
                RenameSelfMultiplier = selfMultiplier ?? r.RenameSelfMultiplier,
            });

        [SubSlashCommand("buyback", "What /buyback costs")]
        public Task<InteractionMessageProperties> BuyBackAsync(
            [SlashCommandParameter(Description = "Price right after being renamed", MinValue = 0, MaxValue = 1_000_000)] double? cost = null,
            [SlashCommandParameter(Name = "min-cost", Description = "Price once the window has passed", MinValue = 0, MaxValue = 1_000_000)] double? minCost = null,
            [SlashCommandParameter(Name = "window-hours", Description = "Hours for the price to fall to the minimum", MinValue = 1, MaxValue = 720)] double? windowHours = null)
            => UpdateAsync(r => r with
            {
                BuyBackCost = cost ?? r.BuyBackCost,
                BuyBackMinCost = minCost ?? r.BuyBackMinCost,
                BuyBackWindowHours = windowHours ?? r.BuyBackWindowHours,
            });

        [SubSlashCommand("shield", "What /shield costs")]
        public Task<InteractionMessageProperties> ShieldAsync(
            [SlashCommandParameter(Name = "cost-per-hour", MinValue = 0, MaxValue = 1_000_000)] double? costPerHour = null,
            [SlashCommandParameter(Name = "max-hours", Description = "Longest shield, counted from now", MinValue = 1, MaxValue = 168)] int? maxHours = null)
            => UpdateAsync(r => r with
            {
                ShieldCostPerHour = costPerHour ?? r.ShieldCostPerHour,
                ShieldMaxHours = maxHours ?? r.ShieldMaxHours,
            });

        [SubSlashCommand("lock", "What /lock and /unlock cost")]
        public Task<InteractionMessageProperties> LockAsync(
            [SlashCommandParameter(Name = "cost-per-hour", MinValue = 0, MaxValue = 1_000_000)] double? costPerHour = null,
            [SlashCommandParameter(Name = "max-hours", MinValue = 1, MaxValue = 168)] int? maxHours = null,
            [SlashCommandParameter(Name = "break-multiplier", Description = "Breaking costs this × the value of the time left", MinValue = 0, MaxValue = 100)] double? breakMultiplier = null)
            => UpdateAsync(r => r with
            {
                LockCostPerHour = costPerHour ?? r.LockCostPerHour,
                LockMaxHours = maxHours ?? r.LockMaxHours,
                LockBreakMultiplier = breakMultiplier ?? r.LockBreakMultiplier,
            });

        [SubSlashCommand("paint", "What /paint and /unpaint cost")]
        public Task<InteractionMessageProperties> PaintAsync(
            [SlashCommandParameter(Name = "cost-per-hour", MinValue = 0, MaxValue = 1_000_000)] double? costPerHour = null,
            [SlashCommandParameter(Name = "max-hours", MinValue = 1, MaxValue = 168)] int? maxHours = null,
            [SlashCommandParameter(Name = "break-multiplier", Description = "Removing early costs this × the value of the time left", MinValue = 0, MaxValue = 100)] double? breakMultiplier = null,
            [SlashCommandParameter(Name = "self-multiplier", Description = "Painting yourself costs this × the price", MinValue = 1, MaxValue = 1000)] double? selfMultiplier = null)
            => UpdateAsync(r => r with
            {
                PaintCostPerHour = costPerHour ?? r.PaintCostPerHour,
                PaintMaxHours = maxHours ?? r.PaintMaxHours,
                PaintBreakMultiplier = breakMultiplier ?? r.PaintBreakMultiplier,
                PaintSelfMultiplier = selfMultiplier ?? r.PaintSelfMultiplier,
            });

        private async Task<InteractionMessageProperties> UpdateAsync(Func<MischiefRules, MischiefRules> change)
        {
            var before = await settings.GetAsync<MischiefRules>(GuildId, MischiefCommands.ModuleId);
            var after = change(before);
            var changed = after != before;
            if (changed)
                await settings.SetAsync(GuildId, MischiefCommands.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

            return Replies.Ephemeral($"""
                {(changed ? "Updated." : "Nothing changed.")}
                ```
                rename   {after.RenameCost} × {after.RenameGrowth}^(renames by others in the last {after.RenameWindowHours} h), cooldown {after.RenameCooldownMinutes} min; yourself {after.RenameCost} × {after.RenameSelfMultiplier}
                buyback  {after.BuyBackCost} right after a rename, falling to {after.BuyBackMinCost} over {after.BuyBackWindowHours} h
                shield   {after.ShieldCostPerHour} per hour, at most {after.ShieldMaxHours} h ahead
                lock     {after.LockCostPerHour} per hour, at most {after.LockMaxHours} h; breaking costs {after.LockBreakMultiplier} × the time left
                paint    {after.PaintCostPerHour} per hour, at most {after.PaintMaxHours} h; removing early costs {after.PaintBreakMultiplier} × the time left; yourself × {after.PaintSelfMultiplier}
                ```
                Prices only apply while the `points` module is on.
                """);
        }
    }
}
