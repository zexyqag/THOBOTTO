namespace THOBOTTO.Panel;

// A server page in the panel's menu: its path under /g/{id}/, its label, where it sits, and what seeing it takes
// (a permission, its module on). Each folder registers its own pages.
public sealed record GuildNavEntry(string Path, string Label, int Order, string? Permission = null, string? Module = null);
