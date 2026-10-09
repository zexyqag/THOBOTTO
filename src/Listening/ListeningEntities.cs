using THOBOTTO.Modules;

namespace THOBOTTO.Listening;

// A member's voice preferences in a server.
public sealed class VoicePreference
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    // The helpers may hear them (voice commands); nobody else's voice is ever decoded.
    public bool Listen { get; set; }

    // A free helper joins them to listen whenever they're in voice (when the server allows it).
    public bool AutoListen { get; set; }

    // A free helper joins them for music whenever they're in voice, and waits (when the server allows it).
    public bool AutoMusic { get; set; }
}

public static class HelperPriorities
{
    public const string MusicFirst = "music";
    public const string ListeningFirst = "listening";
    public const string FirstCome = "first";
}

// Stored with SettingsStore under the listening module's id.
public sealed record ListeningRules
{
    [Setting("Members may have a helper join them to listen", Help = "With this on, members can have a free helper come and listen whenever they're in voice; otherwise they summon one with /listen.")]
    public bool AutoListenAllowed { get; init; }

    [Setting("When helpers run out", Choices = [HelperPriorities.MusicFirst + "=Music first: a listening helper starts playing", HelperPriorities.ListeningFirst + "=Listening first: a playing helper stops to listen", HelperPriorities.FirstCome + "=First come, first served: nothing is taken over"])]
    public string Priority { get; init; } = HelperPriorities.MusicFirst;
}
