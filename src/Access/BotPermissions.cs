using NetCord;

namespace THOBOTTO.Access;

// Discord is the Discord permission that also allows it, when a server follows Discord's permissions.
public sealed record BotPermission(string Id, string Description, Permissions Discord);

// What the bot lets people do. Granted to Discord roles with /perms; the server owner has all of them.
public static class BotPermissions
{
    public const string ManagePermissions = "perms.manage";
    public const string ManageModules = "modules.manage";
    public const string ManageVoiceHubs = "voice.hubs";
    public const string ManageServers = "servers.manage";
    public const string ModerateNicknames = "members.nick";
    public const string ModWarn = "mod.warn";
    public const string ModTimeout = "mod.timeout";
    public const string ModKick = "mod.kick";
    public const string ModBan = "mod.ban";
    public const string ModMessages = "mod.messages";
    public const string ModChannels = "mod.channels";
    public const string ModVoice = "mod.voice";
    public const string ModRoles = "mod.roles";
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
        new(ManagePermissions, "Grant and revoke bot permissions", Permissions.Administrator),
        new(ManageModules, "Turn modules on or off", Permissions.ManageGuild),
        new(ManageVoiceHubs, "Add and remove dynamic voice hubs", Permissions.ManageChannels),
        new(ManageServers, "Server board settings, remove anyone's server, no add limits", Permissions.ManageGuild),
        new(ModerateNicknames, "Change the nickname of members ranked below you (moderation, not /rename)", Permissions.ManageNicknames),
        new(ModWarn, "Warn members below you, add notes, see moderation history", Permissions.ModerateUsers),
        new(ModTimeout, "Time out members below you, and lift timeouts", Permissions.ModerateUsers),
        new(ModKick, "Kick members below you", Permissions.KickUsers),
        new(ModBan, "Ban (also for a while) and unban", Permissions.BanUsers),
        new(ModMessages, "Delete and purge messages", Permissions.ManageMessages),
        new(ModChannels, "Slowmode, lock and unlock channels", Permissions.ManageChannels),
        new(ModVoice, "Move, disconnect, server mute and deafen members below you", Permissions.MoveUsers),
        new(ModRoles, "Give and take roles below your own, also for a while", Permissions.ManageRoles),
        new(ModManage, "Moderation settings, and change or pardon anyone's case", Permissions.ManageGuild),
        new(ManagePoints, "Change how points are earned, and add or take points", Permissions.ManageGuild),
        new(ManageMischief, "Set mischief prices and cooldowns", Permissions.ManageGuild),
        new(ManageFame, "Set up the hall of fame", Permissions.ManageGuild),
        new(ManageBets, "Resolve, cancel or revert anyone's bet", Permissions.ManageMessages),
        new(ManageQuotes, "Delete anyone's quotes", Permissions.ManageMessages),
        new(ManageExpressions, "Set up member-made emojis and stickers", Permissions.ManageGuildExpressions),
        new(ManageArchive, "See the archive's status and settings", Permissions.ViewAuditLog),
        new(PurgeArchive, "Delete archived content for good (with a reason)", Permissions.Administrator),
        new(ManageEvents, "Event settings, and cancel anyone's event", Permissions.ManageEvents),
        new(CreateEvents, "Plan events, when planning is limited to it", Permissions.CreateEvents),
        new(ManageGames, "Add and remove games, post role pickers, game settings", Permissions.ManageRoles),
        new(ManageMusic, "Music settings", Permissions.ManageGuild),
        new(MusicDj, "Control music others queued, when that's limited", Permissions.MoveUsers),
    ];

    public static BotPermission? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    // One permission, or a group by its prefix: "mod.*" is every mod.… permission.
    public static IReadOnlyList<BotPermission> Matching(string pattern)
        => pattern.EndsWith(".*") ? All.Where(p => p.Id.StartsWith(pattern[..^1])).ToList() : Find(pattern) is { } one ? [one] : [];

    // "mod.*" and the like, for prefixes several permissions share.
    public static IEnumerable<(string Pattern, int Count)> Groups
        => All.GroupBy(p => p.Id[..(p.Id.IndexOf('.') + 1)]).Where(g => g.Count() > 1).Select(g => (g.Key + "*", g.Count()));
}
