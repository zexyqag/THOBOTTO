using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Gate;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

public sealed partial class ModCommands
{
    [SubSlashCommand("raid", "Raid mode: hold newcomers, pause invites (needs mod.kick)")]
    [RequirePermission(BotPermissions.ModKick)]
    public sealed class RaidCommands(Gatekeeper gate, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [SubSlashCommand("on", "Turn raid mode on now")]
        public async Task<InteractionMessageProperties> OnAsync()
            => await OffAsync() ?? Replies.Ephemeral(await gate.StartRaidAsync(Context.Guild!.Id, (GuildUser)Context.User, "turned on by hand", []));

        [SubSlashCommand("off", "End raid mode")]
        public async Task<InteractionMessageProperties> OffNowAsync()
            => await OffAsync() ?? Replies.Ephemeral(await gate.EndRaidAsync(Context.Guild!.Id, (GuildUser)Context.User));

        [SubSlashCommand("status", "Whether raid mode is on")]
        public async Task<InteractionMessageProperties> StatusAsync()
            => await OffAsync() ?? Replies.Ephemeral(await gate.RaidAsync(Context.Guild!.Id) is { } raid
                ? $"Raid mode has been on since <t:{raid.StartedAt.ToUnixTimeSeconds()}:R>; the last join was <t:{raid.LastJoinAt.ToUnixTimeSeconds()}:R>."
                : "Raid mode is off.");

        private async Task<InteractionMessageProperties?> OffAsync()
            => await modules.IsEnabledAsync(Context.Guild!.Id, Gatekeeper.ModuleId) ? null : Replies.Ephemeral($"The `{Gatekeeper.ModuleId}` module is off.");
    }
}
