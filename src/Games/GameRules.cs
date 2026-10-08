namespace THOBOTTO.Games;

// Stored with SettingsStore under the module id.
public sealed record GameRules
{
    // Only members with a game's role may start its sessions.
    public bool SessionsNeedRole { get; init; }

    // Whether pinging a game role offers to make it a session.
    public bool OfferSessions { get; init; } = true;
}
