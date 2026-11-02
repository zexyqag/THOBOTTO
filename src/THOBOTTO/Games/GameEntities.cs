using THOBOTTO.Events;

namespace THOBOTTO.Games;

public sealed class Game
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    public string? Emoji { get; set; }

    // Members who want to hear about the game have this role; sessions ping it.
    public ulong RoleId { get; set; }

    // The game's game server, shown on its sessions.
    public long? ServerId { get; set; }

    // Where its sessions go by default.
    public ulong? ChannelId { get; set; }

    // How many can play together (5 for a 5v5 team), the default limit on its sessions.
    public int? Players { get; set; }

    // Null follows the server's colour for game roles.
    public int? Colour { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

// A way to play a game with its own player count, e.g. Wingman (2) next to Competitive (5).
public sealed class GameMode
{
    public long Id { get; init; }

    public long GameId { get; init; }

    public required string Name { get; set; }

    public int Players { get; set; }
}

// A session proposed in a group DM (through the bot installed to its proposer's account), voted on there, and planned
// in the server by its proposer. With several games, the group votes on the game too.
public sealed class GroupProposal
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    // Suggested games are added at the end.
    public required long[] GameIds { get; set; }

    public ulong ProposerId { get; init; }

    public required string ProposerName { get; init; }

    // Null: the planned game's usual title.
    public string? Title { get; init; }

    // Suggested times are added at the end.
    public required DateTimeOffset[] Times { get; set; }

    // How it's settled (Deciding), how many it takes as soon as enough can make a time (null: the game's player count,
    // else the server's number), and when it closes.
    public string Decide { get; init; } = Deciding.WhenItCloses;

    public int? Enough { get; init; }

    public DateTimeOffset ClosesAt { get; init; }

    // How a suggested time moves the session it becomes (Moving); null: the server's setting.
    public string? Move { get; init; }

    // What the group may suggest (Suggesting), here and for the session it becomes.
    public string Suggestions { get; init; } = Suggesting.TimesAndGames;

    // The session it became, at which of its times.
    public long? EventId { get; set; }

    public int? Planned { get; set; }

    // Settled without a session: nobody could make any time still ahead.
    public bool Closed { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

// A time someone in the group can make (its index in Times), or Can't (-1). By name: not everyone there is in the server.
public sealed class GroupProposalVote
{
    public long ProposalId { get; init; }

    public ulong UserId { get; init; }

    public int Choice { get; init; }

    public required string Name { get; set; }
}

// A game of the proposal someone in the group would play.
public sealed class GroupProposalGameVote
{
    public long ProposalId { get; init; }

    public ulong UserId { get; init; }

    public long GameId { get; init; }

    public required string Name { get; set; }
}

// Who in a group DM wants to see what they can all play, gathered by pressing (the bot isn't told who's in a group).
public sealed class GroupLineup
{
    public long Id { get; init; }

    public ulong StarterId { get; init; }

    // The games it showed last, to propose a session in.
    public string[] Shown { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class GroupLineupMember
{
    public long LineupId { get; init; }

    public ulong UserId { get; init; }

    public required string Name { get; set; }
}
