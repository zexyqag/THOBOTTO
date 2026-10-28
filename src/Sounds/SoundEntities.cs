using THOBOTTO.Access;
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

    // The file as uploaded (MP3, OGG or WAV), so it can go to Discord as it is; dropped a while after a sound is
    // removed or rejected, the record staying.
    public byte[]? Audio { get; set; }

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

    // Put on Discord's soundboard by a manager: stays there whatever the plays say.
    public bool Pinned { get; set; }

    // Someone took it off Discord's soundboard (there, or a manager through the bot): it isn't wanted there until put back by hand.
    public DateTimeOffset? DiscordRemovedAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    public int Plays { get; set; }

    public DateTimeOffset? LastPlayedAt { get; set; }
}

// One play of a library sound, by a helper or from Discord's own soundboard.
public sealed class SoundPlay
{
    public long Id { get; init; }

    public long SoundId { get; init; }

    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public DateTimeOffset At { get; init; }

    public bool OnDiscord { get; init; }
}

public sealed class SoundVote
{
    public long SoundId { get; init; }

    public ulong UserId { get; init; }

    public bool Up { get; set; }
}

// The sound a member's arrival in a voice channel plays.
// One of a member's join sounds; with several, one plays at random.
public sealed class JoinSound
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public long SoundId { get; init; }
}

public static class BoardChoices
{
    public const string Managers = "managers";
    public const string MostPlayed = "most-played";
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

    [Setting("Most sounds a minute in a channel", Help = "Everyone's together; 0 is no limit.", Unit = "sounds", Min = 0, Max = 120)]
    public int ChannelPerMinute { get; init; }

    [Setting("Playing needs " + BotPermissions.SoundsPlay, Help = "Off: anyone in the channel may play a sound. On: only those with the permission (the Permissions page). Join sounds aren't affected.")]
    public bool PlayNeedsPermission { get; init; }

    [Setting("In step with Discord's soundboard", Help = "Sounds uploaded on Discord come into the library as they appear, names follow both ways, and a sound removed from the library leaves Discord's soundboard too. Off: only /sound import moves anything.")]
    public bool DiscordSync { get; init; } = true;

    [Setting("Keep removed and rejected sounds' audio for", Help = "After that only the record stays; 0 keeps the audio for good.", Unit = "days", Min = 0, Max = 365)]
    public int KeepRemovedDays { get; init; } = 30;

    [Setting("Discord's soundboard shows", Help = "What managers pin there with /sound discord, or, around the pinned ones, the library's most played sounds that fit Discord's limits. A sound taken off Discord by hand stays off until pinned.", Choices = [BoardChoices.Managers + "=What managers put there", BoardChoices.MostPlayed + "=The most played, around the pinned ones"])]
    public string DiscordBoard { get; init; } = BoardChoices.Managers;

    [Setting("Most played: plays counted over", Unit = "days", Min = 1, Max = 365)]
    public int MostPlayedDays { get; init; } = 30;

    [Setting("Most played: a sound needs this much more plays to replace one", Help = "So the board doesn't churn under people's fingers.", Unit = "%", Min = 0, Max = 500)]
    public int SwapMargin { get; init; } = 25;

    [Setting("Most played: swaps a day at most", Help = "Filling a free slot isn't a swap.", Unit = "swaps", Min = 0, Max = 48)]
    public int SwapsPerDay { get; init; } = 2;

    [Setting("Most played: a newly accepted sound holds a free slot for", Help = "To earn plays; 0 gives new sounds no head start.", Unit = "days", Min = 0, Max = 90)]
    public int TrialDays { get; init; } = 7;

    [Setting("Join sounds", Help = "Members pick a sound that plays when they join a voice channel (no Nitro needed).")]
    public bool JoinSounds { get; init; } = true;

    [Setting("A member's join sound plays at most every", Help = "So reconnecting doesn't replay it.", Unit = "minutes", Min = 0, Max = 1440)]
    public int JoinCooldownMinutes { get; init; } = 10;

    [Setting("Where no helper is in the channel", Choices = [NoHelperChoices.HopIn + "=A free helper hops in, plays it, and leaves", NoHelperChoices.Skip + "=Nothing plays"])]
    public string NoHelper { get; init; } = NoHelperChoices.HopIn;
}
