using THOBOTTO.Modules;

namespace THOBOTTO.Sounds;

public static class SoundStates
{
    public const string Voting = "voting";
    public const string Rejected = "rejected";
    // In the library: anyone can play it.
    public const string Library = "library";
    public const string Removed = "removed";
}

// A sound in the server's own library, from proposal on. On Discord's soundboard too when DiscordId is set.
public sealed class Sound
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    public ulong CreatorId { get; init; }

    public ulong ProposerId { get; init; }

    // The file as uploaded (MP3, OGG or WAV), so it can go to Discord as it is.
    public required byte[] Audio { get; init; }

    public required string FileName { get; init; }

    public int Milliseconds { get; init; }

    public string State { get; set; } = SoundStates.Voting;

    public double Paid { get; init; }

    public ulong? VoteChannelId { get; set; }

    public ulong? VoteMessageId { get; set; }

    public DateTimeOffset ProposedAt { get; init; }

    public DateTimeOffset VoteEndsAt { get; init; }

    public DateTimeOffset? DecidedAt { get; set; }

    public ulong? DiscordId { get; set; }

    public int Plays { get; set; }

    public DateTimeOffset? LastPlayedAt { get; set; }
}

public sealed class SoundVote
{
    public long SoundId { get; init; }

    public ulong UserId { get; init; }

    public bool Up { get; set; }
}

// The sound a member's arrival in a voice channel plays.
public sealed class JoinSound
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public long SoundId { get; set; }
}

public static class NoHelperChoices
{
    public const string HopIn = "hop";
    public const string Skip = "skip";
}

// Stored with SettingsStore under the module id. Proposals use the emoji and sticker voting settings.
public sealed record SoundRules
{
    [Setting("Longest sound", Unit = "seconds", Min = 1, Max = 60)]
    public int MaxSeconds { get; init; } = 15;

    [Setting("Between a member's sounds", Help = "So nobody plays one after another.", Unit = "seconds", Min = 0, Max = 3600)]
    public int CooldownSeconds { get; init; } = 10;

    [Setting("Join sounds", Help = "Members pick a sound that plays when they join a voice channel (no Nitro needed).")]
    public bool JoinSounds { get; init; } = true;

    [Setting("A member's join sound plays at most every", Help = "So reconnecting doesn't replay it.", Unit = "minutes", Min = 0, Max = 1440)]
    public int JoinCooldownMinutes { get; init; } = 10;

    [Setting("Where no helper is in the channel", Choices = [NoHelperChoices.HopIn + "=A free helper hops in, plays it, and leaves", NoHelperChoices.Skip + "=Nothing plays"])]
    public string NoHelper { get; init; } = NoHelperChoices.HopIn;
}
