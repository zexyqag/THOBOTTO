using NetCord;

namespace THOBOTTO.Discord;

// What the bot's application allows, as read at startup (the intents and Activities turned on in the Developer
// Portal); changes there take a restart.
public sealed record DiscordApp(ApplicationFlags Flags)
{
    public bool Members => (Flags & (ApplicationFlags.GatewayGuildUsers | ApplicationFlags.GatewayGuildUsersLimited)) != 0;

    public bool Presences => (Flags & (ApplicationFlags.GatewayPresence | ApplicationFlags.GatewayPresenceLimited)) != 0;

    public bool Activities => (Flags & ApplicationFlags.Embedded) != 0;
}
