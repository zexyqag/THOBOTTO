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

    [Setting("A vote on a session's time is settled", Help = "When planning with several times, here or from a group DM. Enough people: the session's player limit, else the Events settings' number.", Choices = [Deciding.WhenItCloses + "=By the most votes when it closes", Deciding.AsSoonAsEnough + "=As soon as a time has enough people", Deciding.PlannerDecides + "=By whoever planned it"])]
    public string SessionDecide { get; init; } = Deciding.AsSoonAsEnough;

    [Setting("Sessions channel", Help = "Optional: where a session goes when its game has no channel of its own. Without it, the happenings forum when it takes game sessions (a post each, pinging the game's role), else where it's planned. The Games page shows where each game's sessions go.", Kind = SettingKind.TextChannel)]
    public ulong? SessionsChannelId { get; init; }

    [Setting("Voice channel for sessions", Help = "Unless a session is planned otherwise: opened shortly before the start, in the events' voice category (Events settings).",
        Choices = [VoiceModes.Open + "=A voice channel", VoiceModes.Locked + "=A voice channel for those who are in", NoVoice + "=None"])]
    public string SessionVoice { get; init; } = VoiceModes.Open;

    public const string NoVoice = "none";

    // The event's voice mode for a session planned without choosing.
    public string? UsualVoice => SessionVoice is VoiceModes.Open or VoiceModes.Locked ? SessionVoice : null;

    [Setting("A question in Discord's onboarding", Help = "New members pick their games while joining: a question in Discord's onboarding (a Community server's) whose answers are the games here, each giving its role. Kept in step as games change; the server's other questions are left as they are. One way: picks are just the games' roles.")]
    public bool InOnboarding { get; init; }

    [Setting("Its question", Help = "The question's title in Discord's onboarding.")]
    public string OnboardingQuestion { get; init; } = "What do you play?";

    [Setting("Colour of game roles", Help = "Like #5865F2. Every game role without a colour of its own (on the Games page) takes it, now and whenever it changes; the roles the bot makes carry no permissions.")]
    public string RoleColour { get; init; } = "#5865F2";

    public static int ColourValue(string text) => int.TryParse(text.Trim().TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var value) && text.Trim().TrimStart('#').Length == 6 ? value : 0x5865F2;
}
