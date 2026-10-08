namespace THOBOTTO.Access;

public sealed record BotPermission(string Id, string Description);

// What the bot lets people do. Granted to Discord roles with /perms; the server owner has all of them.
public static class BotPermissions
{
    public const string ManagePermissions = "perms.manage";
    public const string ManageModules = "modules.manage";
    public const string ManageVoiceHubs = "voice.hubs";
    public const string ManageServers = "servers.manage";
    public const string ModerateNicknames = "members.nick";

    public static IReadOnlyList<BotPermission> All { get; } =
    [
        new(ManagePermissions, "Grant and revoke bot permissions"),
        new(ManageModules, "Turn modules on or off"),
        new(ManageVoiceHubs, "Add and remove dynamic voice hubs"),
        new(ManageServers, "Server board settings, remove anyone's server, no add limits"),
        new(ModerateNicknames, "Change the nickname of members ranked below you (moderation, not /rename)"),
    ];

    public static BotPermission? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
