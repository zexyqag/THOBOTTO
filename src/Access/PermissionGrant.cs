namespace THOBOTTO.Access;

// A row means the role has the permission in that guild.
public sealed class PermissionGrant
{
    public ulong GuildId { get; init; }

    public ulong RoleId { get; init; }

    public required string Permission { get; init; }
}
