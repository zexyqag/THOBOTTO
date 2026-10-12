namespace THOBOTTO.Panel;

// A page of the bot's own in the owner's menu: its path, its label, and where it sits. Each folder registers its own.
public sealed record BotNavEntry(string Path, string Label, int Order);
