namespace THOBOTTO.Members;

// Tells everyone who keeps a member's preferences for a server that the member left it, each on its own: one
// failing stops neither the next nor the gateway.
public sealed class MemberLeaving(IEnumerable<IMemberLeft> keepers, ILogger<MemberLeaving> logger)
{
    public async Task LeftAsync(ulong guildId, ulong userId)
    {
        foreach (var keeper in keepers)
        {
            try
            {
                await keeper.MemberLeftAsync(guildId, userId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Keeper} couldn't forget {UserId}, who left {GuildId}", keeper.GetType().Name, userId, guildId);
            }
        }
    }
}
