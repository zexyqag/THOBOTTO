using NetCord.Gateway;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

namespace THOBOTTO.Discord;

// Modules whose interactions only come from servers (the command shells say Contexts = Guild, and the buttons and
// modals sit on messages posted in one), so the guild is always there.
public abstract class GuildCommandModule : ApplicationCommandModule<ApplicationCommandContext>
{
    protected Guild Guild => Context.Guild!;

    protected ulong GuildId => Guild.Id;
}

public abstract class GuildButtonModule : ComponentInteractionModule<ButtonInteractionContext>
{
    protected Guild Guild => Context.Guild!;

    protected ulong GuildId => Guild.Id;
}

public abstract class GuildModalModule : ComponentInteractionModule<ModalInteractionContext>
{
    protected Guild Guild => Context.Guild!;

    protected ulong GuildId => Guild.Id;
}
