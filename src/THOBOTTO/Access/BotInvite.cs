using NetCord;

namespace THOBOTTO.Access;

// What the main bot asks for when it's added to a server: what its features use, not Administrator.
// Rank still matters: its role has to sit above the roles it manages and the members it moderates.
public static class BotInvite
{
    public const Permissions Needed =
        // Reading and talking: commands, events, boards, quotes, the archive.
        Permissions.ViewChannel | Permissions.SendMessages | Permissions.SendMessagesInThreads | Permissions.EmbedLinks
        | Permissions.AttachFiles | Permissions.ReadMessageHistory | Permissions.AddReactions | Permissions.UseExternalEmojis
        // Pinging roles that aren't mentionable (events, sessions).
        | Permissions.MentionEveryone
        // Moderation: purge, timeouts, kicks, bans, voice, nicknames, and seeing who did what.
        | Permissions.ManageMessages | Permissions.ModerateUsers | Permissions.KickUsers | Permissions.BanUsers
        | Permissions.MoveUsers | Permissions.MuteUsers | Permissions.DeafenUsers | Permissions.ManageNicknames | Permissions.ViewAuditLog
        // Game roles, name colours, timed roles, channel locks; dynamic and event voice channels.
        | Permissions.ManageRoles | Permissions.ManageChannels | Permissions.Connect
        // AutoMod rules.
        | Permissions.ManageGuild
        // Member-made emojis and stickers; Discord events.
        | Permissions.ManageGuildExpressions | Permissions.CreateGuildExpressions | Permissions.ManageEvents | Permissions.CreateEvents;

    public static string Url(ulong botId)
        => $"https://discord.com/oauth2/authorize?client_id={botId}&scope=bot+applications.commands&permissions={(ulong)Needed}";
}
