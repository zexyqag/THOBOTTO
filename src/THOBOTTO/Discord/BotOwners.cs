using NetCord.Rest;

namespace THOBOTTO.Discord;

// The bot's owner (or its team's owner), to tell things that are theirs to know.
public sealed class BotOwners(RestClient rest, ILogger<BotOwners> logger)
{
    public async Task DmAsync(string content)
    {
        try
        {
            var application = await rest.GetCurrentApplicationAsync();
            ulong[] owners = application.Team is { } team ? [team.OwnerId] : application.Owner is { } owner ? [owner.Id] : [];
            foreach (var ownerId in owners)
            {
                var dm = await rest.GetDMChannelAsync(ownerId);
                await dm.SendMessageAsync(new() { Content = content.Length <= 2000 ? content : content[..1999] + "…", AllowedMentions = AllowedMentionsProperties.None });
            }
        }
        catch (RestException ex)
        {
            logger.LogWarning("Telling the bot's owner: {Message}", ex.Message);
        }
    }
}
