namespace THOBOTTO.Data;

// A row means the module is on in that guild.
public sealed class EnabledModule
{
    public ulong GuildId { get; init; }

    public required string Module { get; init; }
}
