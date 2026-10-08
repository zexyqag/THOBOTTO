using THOBOTTO.Modules;

namespace THOBOTTO.Games;

// Stored with SettingsStore under the module id.
public sealed record GameRules
{
    [Setting("Sessions need the game's role", Help = "Only members with a game's role may start its sessions.")]
    public bool SessionsNeedRole { get; init; }

    [Setting("Offer a session when a game role is pinged")]
    public bool OfferSessions { get; init; } = true;
}
