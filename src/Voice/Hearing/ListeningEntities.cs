using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Hearing;

public static class TalkTimeModes
{
    public const string Off = "off";
    public const string WhereHelpersAre = "helpers";
    public const string Everywhere = "everywhere";
}

// How long a member talked in voice on a day (UTC), for Wrapped: from audio arriving, never decoded.
public sealed class TalkTime
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public DateOnly Day { get; init; }

    public long Ms { get; set; }
}

// Stored with SettingsStore under the voice commands module's id.
public sealed record ListeningRules
{
    [Setting("Members may have a helper join them to listen", Help = "With this on, members can have a free helper come and listen whenever they're in voice; otherwise they summon one with /listen.")]
    public bool AutoListenAllowed { get; init; }

    [Setting("Voice debugging", Help = "Keeps what happens to each sentence (what was heard, what was understood, how long each step took) for the server's Debug page, so you can see why a command was slow or misheard. In memory only; members are told while it's on.")]
    public bool Debugging { get; init; }

    [Setting("Count talk time", Help = "How long members talk, for Wrapped (from audio arriving; nothing said is decoded). Everywhere: a free helper sits in every voice channel with people in it, which works best with the voice relay on.", Choices = [TalkTimeModes.Off + "=Off", TalkTimeModes.WhereHelpersAre + "=Where a helper already is (playing through the relay, or listening)", TalkTimeModes.Everywhere + "=Everywhere: a helper sits in each voice channel with people"])]
    public string TalkTime { get; init; } = TalkTimeModes.Off;
}
