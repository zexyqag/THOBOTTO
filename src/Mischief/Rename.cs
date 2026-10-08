namespace THOBOTTO.Mischief;

// One /rename, kept as history: phase 5 prices and cools down renames by how often a target was renamed.
public sealed class Rename
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ActorId { get; init; }

    public ulong TargetId { get; init; }

    public string? OldName { get; init; }

    // Null means the nickname was reset.
    public string? NewName { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
