namespace THOBOTTO.Modules;

// A module's per-guild settings as one JSON document, so new settings don't need a migration.
public sealed class ModuleSettings
{
    public ulong GuildId { get; init; }

    public required string Module { get; init; }

    public required string Json { get; set; }
}
