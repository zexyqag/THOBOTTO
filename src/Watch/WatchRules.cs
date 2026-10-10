using THOBOTTO.Modules;

namespace THOBOTTO.Watch;

public static class WatchControl
{
    public const string Anyone = "anyone";
    public const string Djs = "djs";
}

// Stored with SettingsStore under the module id.
public sealed record WatchRules
{
    [Setting("Video quality", Help = "Higher looks better and needs more of the server's upload: about 2.5 Mbit/s per viewer at 720p, 5 at 1080p.", Choices = ["480=480p", "720=720p", "1080=1080p"])]
    public string Quality { get; init; } = "720";

    [Setting("Longest video", Unit = "minutes", Min = 1, Max = 600)]
    public int MaxMinutes { get; init; } = 240;

    [Setting("Who controls playback", Choices = [WatchControl.Anyone + "=Anyone watching", WatchControl.Djs + "=Those with music.dj"])]
    public string Control { get; init; } = WatchControl.Anyone;
}
