using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Points;

public sealed partial class PointsCommands
{
    [SubSlashCommand("give", "Give some of your points to someone (kudos)")]
    public async Task<InteractionMessageProperties> GiveAsync(
        [SlashCommandParameter(Description = "Who to thank")] GuildUser user,
        [SlashCommandParameter(Description = "How many points", MinValue = 1, MaxValue = 1_000_000)] int amount,
        [SlashCommandParameter(Description = "What for", MaxLength = 200)] string? reason = null)
    {
        var (given, text) = await points.GiveAsync(Context.Guild!.Id, Context.User.Id, user.Id, user.IsBot, amount, reason);
        return given
            ? new() { Content = text, AllowedMentions = AllowedMentionsProperties.None }
            : Replies.Ephemeral(text);
    }
}
