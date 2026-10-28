using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO.Activity.Watch;

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

    [Setting("Who adds videos", Choices = [WatchControl.Anyone + "=Anyone in the channel", WatchControl.Djs + "=Those with music.dj"])]
    public string Adding { get; init; } = WatchControl.Anyone;

    [Setting("Who controls playback", Choices = [WatchControl.Anyone + "=Anyone watching", WatchControl.Djs + "=Those with music.dj"])]
    public string Control { get; init; } = WatchControl.Anyone;

    [Setting("Votes on queued videos", Help = "Anyone watching votes for queued videos; reordering puts the most voted first once they have enough votes.", Choices = [QueueVoting.Count + "=Counted and shown", QueueVoting.Reorder + "=Reorder the queue by them"])]
    public string VideoVotes { get; init; } = QueueVoting.Reorder;

    [Setting("Votes to move a video", Help = "With reordering: a video needs this many votes before it moves ahead.", Min = 1, Max = 100)]
    public int VotesToMove { get; init; } = 2;
}
