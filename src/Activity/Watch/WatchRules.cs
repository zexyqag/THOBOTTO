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

    [Setting("Cost to add", Help = "Points for each video a member adds beyond their free ones for the day; nothing while the points module is off.", Unit = "points", Min = 0, Max = 1000)]
    public double AddCost { get; init; } = 1;

    [Setting("Free adds a day", Help = "Each member's first adds of the day cost nothing.", Min = 0, Max = 1000)]
    public int FreeAddsPerDay { get; init; } = 50;

    [Setting("More for each of your videos waiting", Help = "Added to the cost for each video the member already has in the queue, so no one fills it alone.", Unit = "points", Min = 0, Max = 1000)]
    public double CostPerWaiting { get; init; } = 1;

    [Setting("Free with this many watching or fewer", Help = "Adding costs nothing while at most this many people are in the channel; 0: never free this way.", Min = 0, Max = 99)]
    public int FreeWithListeners { get; init; } = 2;

    [Setting("Who adds videos", Choices = [WatchControl.Anyone + "=Anyone in the channel", WatchControl.Djs + "=Those with music.dj"])]
    public string Adding { get; init; } = WatchControl.Anyone;

    [Setting("Who controls playback", Choices = [WatchControl.Anyone + "=Anyone watching", WatchControl.Djs + "=Those with music.dj"])]
    public string Control { get; init; } = WatchControl.Anyone;

    [Setting("Votes to skip", Help = "When only DJs control playback, the others skip by vote: this share of those watching. Whoever added a video can always skip it.", Unit = "% of those watching", Min = 0, Max = 100)]
    public int SkipVotePercent { get; init; } = 50;

    [Setting("Votes on queued videos", Help = "Anyone watching votes for queued videos; reordering puts the most voted first once they have enough votes.", Choices = [QueueVoting.Count + "=Counted and shown", QueueVoting.Reorder + "=Reorder the queue by them"])]
    public string VideoVotes { get; init; } = QueueVoting.Reorder;

    [Setting("Votes to move a video", Help = "With reordering: a video needs this many votes before it moves ahead.", Min = 1, Max = 100)]
    public int VotesToMove { get; init; } = 2;
}
