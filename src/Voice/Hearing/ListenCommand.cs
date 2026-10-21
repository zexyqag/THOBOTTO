using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Hearing;

public sealed class ListenCommand(VoiceEars ears, ModuleState modules) : GuildCommandModule
{
    [SlashCommand("listen", "Summon a helper to your voice channel to hear voice commands (or send it away)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> ListenAsync([SlashCommandParameter(Description = "Send it away instead")] bool stop = false)
    {
        if (!await modules.IsEnabledAsync(GuildId, VoiceEars.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(VoiceEars.ModuleId));
        return new()
        {
            Content = stop ? await ears.DismissAsync(GuildId, Context.User.Id) : await ears.SummonAsync(GuildId, Context.User.Id),
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
