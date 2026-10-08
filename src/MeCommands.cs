using NetCord;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO;

// A member's own preferences, from any module that has some (as part of this class, hence in this
// namespace).
[SlashCommand("me", "Your own preferences: time zone, DMs", Contexts = [InteractionContextType.Guild])]
public sealed partial class MeCommands(IServiceProvider services) : ApplicationCommandModule<ApplicationCommandContext>
{
    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
}
