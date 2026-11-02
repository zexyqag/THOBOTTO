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
// in the server by its proposer.
public sealed class GroupProposal
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public long GameId { get; init; }

    public ulong ProposerId { get; init; }

    public required string ProposerName { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset[] Times { get; init; }

    // The session it became, at which of its times.
    public long? EventId { get; set; }

    public int? Planned { get; set; }

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
