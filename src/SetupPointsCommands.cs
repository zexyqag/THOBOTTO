using NetCord;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO;

// Points and mischief prices: their own admin command, next to /setup, because Discord caps one
// command's size and these two would push /setup over it. Hidden like /setup.
[SlashCommand("setup-points", "Set up points and mischief prices (admins)", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ManageGuild)]
public sealed partial class SetupPointsCommands : ApplicationCommandModule<ApplicationCommandContext>;
