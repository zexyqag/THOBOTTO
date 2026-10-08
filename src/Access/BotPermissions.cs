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
    public const string ManagePoints = "points.manage";
    public const string ManageMischief = "mischief.manage";
    public const string ManageFame = "fame.manage";
    public const string ManageBets = "bets.manage";
    public const string ManageQuotes = "quotes.manage";

    public static IReadOnlyList<BotPermission> All { get; } =
    [
        new(ManagePermissions, "Grant and revoke bot permissions"),
        new(ManageModules, "Turn modules on or off"),
        new(ManageVoiceHubs, "Add and remove dynamic voice hubs"),
        new(ManageServers, "Server board settings, remove anyone's server, no add limits"),
        new(ModerateNicknames, "Change the nickname of members ranked below you (moderation, not /rename)"),
        new(ManagePoints, "Change how points are earned, and add or take points"),
        new(ManageMischief, "Set mischief prices and cooldowns"),
        new(ManageFame, "Set up the hall of fame"),
        new(ManageBets, "Resolve, cancel or revert anyone's bet"),
        new(ManageQuotes, "Delete anyone's quotes"),
    ];

    public static BotPermission? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
