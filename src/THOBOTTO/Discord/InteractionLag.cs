using NetCord;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Discord;

// Discord drops an interaction not answered within 3 seconds of being made ("Unknown interaction"). One reaching the
// bot late says so, telling a slow delivery apart from a slow answer.
public sealed class InteractionLag(TimeProvider time, ILogger<InteractionLag> logger) : IInteractionCreateGatewayHandler
{
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(2);

    public ValueTask HandleAsync(Interaction interaction)
    {
        var lag = time.GetUtcNow() - interaction.CreatedAt;
        if (lag > Late)
            logger.LogWarning("Interaction {Name} reached the bot {Lag:0.0} s after it was made", Name(interaction), lag.TotalSeconds);
        return default;
    }

    private static string Name(Interaction interaction) => interaction switch
    {
        ApplicationCommandInteraction command => command.Data.Name,
        MessageComponentInteraction component => component.Data.CustomId,
        ModalInteraction modal => modal.Data.CustomId,
        _ => interaction.GetType().Name,
    };
}
