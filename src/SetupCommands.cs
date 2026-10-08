using NetCord;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO;

// Everything an admin sets up, in one place. Each module adds its group or command from its own
// folder (as part of this class, hence in this namespace). Hidden by default from members without
// Manage Server; the server can show it to other roles in its Integrations settings. The bot's own
// permission checks apply either way.
[SlashCommand("setup", "Set up the bot (admins)", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ManageGuild)]
public sealed partial class SetupCommands(IServiceProvider services) : ApplicationCommandModule<ApplicationCommandContext>
{
    // Modules' single commands here need their own services; groups get theirs injected.
    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private ulong GuildId => Context.Guild!.Id;
}
