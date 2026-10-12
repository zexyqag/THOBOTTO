using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Hearing;

public sealed class ListenCommand(VoiceEars ears, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("listen", "Summon a helper to your voice channel to hear voice commands (or send it away)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> ListenAsync([SlashCommandParameter(Description = "Send it away instead")] bool stop = false)
    {
        var guildId = Context.Guild!.Id;
        if (!await modules.IsEnabledAsync(guildId, VoiceEars.ModuleId))
            return Replies.Ephemeral($"The `{VoiceEars.ModuleId}` module is off here.");
        return new()
        {
            Content = stop ? await ears.DismissAsync(guildId, Context.User.Id) : await ears.SummonAsync(guildId, Context.User.Id),
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
