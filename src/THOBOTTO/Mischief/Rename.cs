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

    // Points paid; 0 when free.
    public double Cost { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    // When the name was in place: the command's time, or for the owner (whom bots can't rename, so they set it
    // themselves) when they did; null while they haven't.
    public DateTimeOffset? DoneAt { get; set; }

    // A day passed without the owner setting it: the asker had the points back, and the rename counts for nothing.
    public DateTimeOffset? DeclinedAt { get; set; }
}
