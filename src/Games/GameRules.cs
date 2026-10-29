using THOBOTTO.Modules;

namespace THOBOTTO.Games;

// Stored with SettingsStore under the module id.
public sealed record GameRules
{
    [Setting("Sessions need the game's role", Help = "Only members with a game's role may start its sessions.")]
    public bool SessionsNeedRole { get; init; }

    [Setting("Offer a session when a game role is pinged")]
    public bool OfferSessions { get; init; } = true;

    [Setting("Colour of game roles", Help = "Like #5865F2. Every game role without a colour of its own (on the Games page) takes it, now and whenever it changes; the roles the bot makes carry no permissions.")]
    public string RoleColour { get; init; } = "#5865F2";

    public static int ColourValue(string text) => int.TryParse(text.Trim().TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var value) && text.Trim().TrimStart('#').Length == 6 ? value : 0x5865F2;
}
