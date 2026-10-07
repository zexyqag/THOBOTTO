namespace THOBOTTO.Data;

public sealed class AuditEntry
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ActorId { get; init; }

    public required string Action { get; init; }

    public string? Details { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
