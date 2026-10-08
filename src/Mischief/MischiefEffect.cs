namespace THOBOTTO.Mischief;

public static class MischiefEffectKinds
{
    // Bought by a member for themselves: nobody can rename or paint them.
    public const string Shield = "shield";

    // Bought by someone else: the target's current name stays.
    public const string Lock = "lock";

    // A colour role on the target, deleted when it ends.
    public const string Paint = "paint";
}

// A paid, timed effect on a member. Active while EndsAt is in the future.
public sealed class MischiefEffect
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Kind { get; init; }

    public ulong TargetId { get; init; }

    public ulong ActorId { get; init; }

    public double Paid { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset EndsAt { get; set; }

    // Who ended it early (an /unlock), if anyone.
    public ulong? EndedById { get; set; }

    // The paint's role; cleared once the role is deleted.
    public ulong? RoleId { get; set; }
}
