using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;

namespace THOBOTTO.Access;

// /sudo: every permission for a while, for the server owner and roles granted perms.sudo. Hidden from members
// without Administrator by default; a server can show it to others in Discord's command settings.
public sealed class SudoCommand(AccessControl access) : GuildCommandModule
{
    [SlashCommand("sudo", "Take every bot permission for a while (the server owner, or roles granted perms.sudo)", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.Administrator)]
    public async Task<InteractionMessageProperties> SudoAsync([SlashCommandParameter(Description = "End it now instead")] bool off = false)
    {
        var member = (GuildUser)Context.User;
        if (off)
        {
            await access.EndSudoAsync(GuildId, member.Id);
            return Replies.Ephemeral("Sudo ended: your roles decide again.");
        }
        if (!await access.MaySudoAsync(Guild, member))
            return Replies.Ephemeral($"That takes `{BotPermissions.Sudo}` from a role, or being the server owner.");
        var until = await access.SudoAsync(Guild, member);
        return Replies.Ephemeral($"⚡ Sudo until <t:{until.ToUnixTimeSeconds()}:t>: every permission counts for you until then. `/sudo off:True` ends it sooner.");
    }
}
