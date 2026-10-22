using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Hearing;

public sealed class ListenCommand(VoiceEars ears, ModuleState modules) : GuildCommandModule
{
    [SlashCommand("listen", "Summon a helper to your voice channel to hear voice commands (or send it away)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> ListenAsync(
        [SlashCommandParameter(Description = "Send it away instead")] bool stop = false,
        [SlashCommandParameter(Description = "Which helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string? helper = null)
    {
        if (!await modules.IsEnabledAsync(GuildId, VoiceEars.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(VoiceEars.ModuleId));
        if (helper is not null && HelperAutocomplete.Id(helper) is null)
            return Replies.Ephemeral("Pick a helper from the list.");
        return new()
        {
            Content = stop ? await ears.DismissAsync(GuildId, Context.User.Id) : await ears.SummonAsync(GuildId, Context.User.Id, HelperAutocomplete.Id(helper)),
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
