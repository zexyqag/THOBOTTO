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
        public async Task<InteractionMessageProperties> RenameAsync(
            [SlashCommandParameter(Description = "Price of the first rename in the window", MinValue = 0, MaxValue = 1_000_000)] double? cost = null,
            [SlashCommandParameter(Description = "Price multiplier per recent rename of the same person", MinValue = 1, MaxValue = 10)] double? growth = null,
            [SlashCommandParameter(Name = "window-hours", Description = "How far back renames count towards the price", MinValue = 0, MaxValue = 720)] double? windowHours = null,
            [SlashCommandParameter(Name = "cooldown-minutes", Description = "Minimum time between renames of the same person", MinValue = 0, MaxValue = 10080)] int? cooldown = null)
        {
            var before = await settings.GetAsync<MischiefRules>(GuildId, MischiefCommands.ModuleId);
            var after = before with
            {
                RenameCost = cost ?? before.RenameCost,
                RenameGrowth = growth ?? before.RenameGrowth,
                RenameWindowHours = windowHours ?? before.RenameWindowHours,
                RenameCooldownMinutes = cooldown ?? before.RenameCooldownMinutes,
            };

            var changed = after != before;
            if (changed)
                await settings.SetAsync(GuildId, MischiefCommands.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

            return Replies.Ephemeral($"""
                {(changed ? "Updated." : "Nothing changed.")}
                Rename: {after.RenameCost} × {after.RenameGrowth}^(renames of that person in the last {after.RenameWindowHours} h), cooldown {after.RenameCooldownMinutes} min.
                Prices only apply while the `points` module is on.
                """);
        }
    }
}
