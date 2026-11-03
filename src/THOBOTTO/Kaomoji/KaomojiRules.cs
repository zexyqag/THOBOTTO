using NetCord;
using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Kaomoji;

// Stored with SettingsStore under the module id.
public sealed record KaomojiRules
{
    [Setting("Post under the member's name", Help = "With their name and avatar, as if they wrote it (the bot needs Manage Webhooks). Off: as the bot's reply, which Discord heads with who used /kaomoji, linked to their account. In DMs it's always the bot's reply.")]
    public bool PostAsMember { get; init; } = true;
}

public sealed class KaomojiSummary(SettingsStore settings, GatewayClient gateway) : ISettingsSummary
{
    public string ModuleId => KaomojiShelf.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        if (!(await settings.GetAsync<KaomojiRules>(guildId, ModuleId)).PostAsMember)
            return "Now: faces are posted as the bot's reply.";
        return gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(gateway.Id, out var bot)
            && (bot.GetPermissions(guild) & (Permissions.Administrator | Permissions.ManageWebhooks)) == 0
            ? "Now: faces are posted as the bot's reply, because it doesn't have Manage Webhooks in this server."
            : "Now: faces are posted under the member's name and avatar.";
    }
}
