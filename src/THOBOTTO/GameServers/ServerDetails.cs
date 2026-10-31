using Microsoft.AspNetCore.DataProtection;

namespace THOBOTTO.GameServers;

// A game server's details beyond its address: checked the same from the panel and /servers edit, the password kept
// protected, and who may see it.
public sealed class ServerDetails(IDataProtectionProvider protection)
{
    public const int NameLength = 100, AddressLength = 200, ModpackLength = 100, VersionLength = 50, LinkLength = 300, NoteLength = 200, PasswordLength = 100;

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.GameServerPasswords");

    public string? Protect(string? password) => password is null ? null : _protector.Protect(password);

    public string? PasswordOf(GameServer server) => server.Password is { } stored ? _protector.Unprotect(stored) : null;

    public string? RconPasswordOf(GameServer server) => server.RconPassword is { } stored ? _protector.Unprotect(stored) : null;

    // Whether a member may have the password: anyone on the card or the button, else the role's members.
    public static bool MaySee(GameServer server, IEnumerable<ulong> memberRoles)
        => server.PasswordFor != PasswordShown.Role || server.PasswordRoleId is { } role && memberRoles.Contains(role);

    // Why the details can't be saved, or null.
    public static string? Problem(string? joinAddress, string? modpackLink, string? passwordFor, ulong? passwordRole)
    {
        if (joinAddress is { } join && !ServerAddress.TryParse(join, out _, out _))
            return "The join address isn't host or host:port.";
        if (modpackLink is { } link && !(Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
            return "The modpack link has to be a web address (https://…).";
        if (passwordFor is not (PasswordShown.Card or PasswordShown.Anyone or PasswordShown.Role))
            return "Pick who gets the password.";
        if (passwordFor == PasswordShown.Role && passwordRole is null)
            return "Pick the role that gets the password.";
        return null;
    }

    // The text as given, trimmed and cut to its length; none for empty.
    public static string? Clean(string? text, int length)
        => text?.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, length)] : null;
}
