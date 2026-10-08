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
    public const string ModWarn = "mod.warn";
    public const string ModManage = "mod.manage";
    public const string ManagePoints = "points.manage";
    public const string ManageMischief = "mischief.manage";
    public const string ManageFame = "fame.manage";
    public const string ManageBets = "bets.manage";
    public const string ManageQuotes = "quotes.manage";
    public const string ManageExpressions = "emojis.manage";
    public const string ManageArchive = "archive.manage";
    public const string PurgeArchive = "archive.purge";
    public const string ManageEvents = "events.manage";
    public const string CreateEvents = "events.create";
    public const string ManageGames = "games.manage";
    public const string ManageMusic = "music.manage";
    public const string MusicDj = "music.dj";

    public static IReadOnlyList<BotPermission> All { get; } =
    [
        new(ManagePermissions, "Grant and revoke bot permissions"),
        new(ManageModules, "Turn modules on or off"),
        new(ManageVoiceHubs, "Add and remove dynamic voice hubs"),
        new(ManageServers, "Server board settings, remove anyone's server, no add limits"),
        new(ModerateNicknames, "Change the nickname of members ranked below you (moderation, not /rename)"),
        new(ModWarn, "Warn members below you, add notes, see moderation history"),
        new(ModManage, "Moderation settings, and change or pardon anyone's case"),
        new(ManagePoints, "Change how points are earned, and add or take points"),
        new(ManageMischief, "Set mischief prices and cooldowns"),
        new(ManageFame, "Set up the hall of fame"),
        new(ManageBets, "Resolve, cancel or revert anyone's bet"),
        new(ManageQuotes, "Delete anyone's quotes"),
        new(ManageExpressions, "Set up member-made emojis and stickers"),
        new(ManageArchive, "See the archive's status and settings"),
        new(PurgeArchive, "Delete archived content for good (with a reason)"),
        new(ManageEvents, "Event settings, and cancel anyone's event"),
        new(CreateEvents, "Plan events, when planning is limited to it"),
        new(ManageGames, "Add and remove games, post role pickers, game settings"),
        new(ManageMusic, "Music settings"),
        new(MusicDj, "Control music others queued, when that's limited"),
    ];

    public static BotPermission? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
