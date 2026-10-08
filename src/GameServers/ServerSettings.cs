namespace THOBOTTO.GameServers;

// Per-guild settings, changed with /setup servers.
public sealed class ServerSettings
{
    public const int MaxServersLimit = 50;
    public const int MinPollSeconds = 30;
    public const int MaxPollSeconds = 3600;
    public const int MaxFailuresBeforeOffline = 10;

    public ulong GuildId { get; init; }

    // The channel the status messages live in; nothing is posted until it's set.
    public ulong? BoardChannelId { get; set; }

    public bool MembersCanAdd { get; set; } = true;

    public int MaxPerMember { get; set; } = 5;

    public int MaxServers { get; set; } = 25;

    public int PollSeconds { get; set; } = 60;

    // One missed reply is common over UDP; don't flap to offline on it.
    public int FailuresBeforeOffline { get; set; } = 2;
}
