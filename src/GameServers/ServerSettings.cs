using THOBOTTO.Access;
using THOBOTTO.Modules;

namespace THOBOTTO.GameServers;

// Per-guild settings, changed on the panel or with /setup set.
public sealed class ServerSettings
{
    public const int MaxServersLimit = 50;
    public const int MinPollSeconds = 30;
    public const int MaxPollSeconds = 3600;
    public const int MaxFailuresBeforeOffline = 10;

    public ulong GuildId { get; init; }

    [Setting("Members can add game servers", Help = "Otherwise only " + BotPermissions.ManageGameServers + ".")]
    public bool MembersCanAdd { get; set; } = true;

    [Setting("Game servers per member", Min = 1, Max = MaxServersLimit)]
    public int MaxPerMember { get; set; } = 5;

    [Setting("Game servers in total", Min = 1, Max = MaxServersLimit)]
    public int MaxServers { get; set; } = 25;

    [Setting("Check every", Unit = "seconds", Min = MinPollSeconds, Max = MaxPollSeconds)]
    public int PollSeconds { get; set; } = 60;

    [Setting("Offline after", Help = "Failed checks in a row; one missed reply is common.", Unit = "failed checks", Min = 1, Max = MaxFailuresBeforeOffline)]
    public int FailuresBeforeOffline { get; set; } = 2;
}
