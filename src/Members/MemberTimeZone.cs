namespace THOBOTTO.Members;

// A member's own time zone, for reading the times they type. Not per guild: it's where they are.
public sealed class MemberTimeZone
{
    public ulong UserId { get; init; }

    public required string Zone { get; set; }
}
