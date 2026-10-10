using THOBOTTO.Modules;

namespace THOBOTTO.Voice;

public static class ChannelNaming
{
    public const string Owner = "owner";
    public const string Activity = "activity";
}

// Stored with SettingsStore under the module id.
public sealed record VoiceRules
{
    [Setting("Most new channels at once", Help = "When this many hub channels exist, people joining a hub stay in it until one empties. 0 is no limit.", Unit = "channels", Min = 0, Max = 100)]
    public int MaxChannels { get; init; }

    [Setting("Name channels after", Help = "With what people do, a channel is renamed once it has held for a few minutes (Discord allows two renames in ten minutes). Games need the Presence Intent, turned on for the bot in the Discord Developer Portal.", Choices = [ChannelNaming.Owner + "=Who made it", ChannelNaming.Activity + "=What people do: the game most of them are playing, else who made it"])]
    public string Naming { get; init; } = ChannelNaming.Owner;
}
