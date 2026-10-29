using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Games;

// The role the bot makes for a game: mentionable, with no permissions of its own (it's only for pinging), in the
// server's colour for them until the game is given its own.
public static class GameRoles
{
    public static async Task<ulong> CreateAsync(RestClient rest, ulong guildId, string name, GameRules rules)
        => (await rest.CreateGuildRoleAsync(guildId, new()
        {
            Name = name,
            Mentionable = true,
            Permissions = 0,
            Colors = new(new Color(GameRules.ColourValue(rules.RoleColour))),
        })).Id;
}
