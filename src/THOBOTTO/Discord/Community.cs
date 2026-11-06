using NetCord.Gateway;

namespace THOBOTTO.Discord;

// Some of Discord's own features (onboarding, rules screening) are a Community server's only.
public static class Community
{
    public static bool IsCommunity(this GatewayClient gateway, ulong guildId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Features.Contains("COMMUNITY");

    public const string Needed = "a Community server (Server Settings → Enable Community)";
}
