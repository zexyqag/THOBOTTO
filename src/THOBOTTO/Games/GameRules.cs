using THOBOTTO.Events;
using THOBOTTO.Modules;

namespace THOBOTTO.Games;

// Stored with SettingsStore under the module id.
public sealed record GameRules
{
    [Setting("Sessions need the game's role", Help = "Only members with a game's role may start its sessions.")]
    public bool SessionsNeedRole { get; init; }

    [Setting("Offer a session when a game role is pinged")]
    public bool OfferSessions { get; init; } = true;

    [Setting("Sessions channel", Help = "Where a session goes when its game has no channel of its own; with neither, where it's planned. A session proposed from a group DM needs one of the two.", Kind = SettingKind.TextChannel)]
    public ulong? SessionsChannelId { get; init; }

    [Setting("Voice channel for sessions", Help = "Unless a session is planned otherwise: opened shortly before the start, in the events' voice category (Events settings).",
        Choices = [VoiceModes.Open + "=A voice channel", VoiceModes.Locked + "=A voice channel for those who are in", NoVoice + "=None"])]
    public string SessionVoice { get; init; } = VoiceModes.Open;

    public const string NoVoice = "none";

    // The event's voice mode for a session planned without choosing.
    public string? UsualVoice => SessionVoice is VoiceModes.Open or VoiceModes.Locked ? SessionVoice : null;

    [Setting("Colour of game roles", Help = "Like #5865F2. Every game role without a colour of its own (on the Games page) takes it, now and whenever it changes; the roles the bot makes carry no permissions.")]
    public string RoleColour { get; init; } = "#5865F2";

    public static int ColourValue(string text) => int.TryParse(text.Trim().TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var value) && text.Trim().TrimStart('#').Length == 6 ? value : 0x5865F2;
}
