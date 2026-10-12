namespace THOBOTTO.Panel;

// The menu's groups: Community, You and Manage, with Records listed inside Manage.
public enum GuildNavGroup
{
    Community,
    You,
    Manage,
    Records,
}

// A server page in the panel's menu: its path under /g/{id}/, its label, the group it sits in and where in it, and what
// seeing it takes (a permission, its module on). Each folder registers its own pages.
public sealed record GuildNavEntry(string Path, string Label, GuildNavGroup Group, int Order, string? Permission = null, string? Module = null);
