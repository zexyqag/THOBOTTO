using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO.Commands;

public sealed partial class MeCommands
{
    [SubSlashCommand("music-join", "A free helper joins you for music whenever you're in voice, ready to play (if the server allows it)")]
    public async Task<InteractionMessageProperties> MusicJoinAsync(
        [SlashCommandParameter(Description = "On or off")] bool on,
        [SlashCommandParameter(Description = "The helper you'd rather have come, when it's free", AutocompleteProviderType = typeof(HelperAutocomplete))] string? helper = null)
    {
        if (on && !(await Get<SettingsStore>().GetAsync<MusicRules>(GuildId, MusicService.ModuleId)).AutoJoinAllowed)
            return Replies.Ephemeral("This server doesn't let helpers join members by themselves.");
        if (helper is not null && HelperAutocomplete.Id(helper) is null)
            return Replies.Ephemeral("Pick a helper from the list.");
        await Get<VoicePrefs>().SetAsync(GuildId, Context.User.Id, p =>
        {
            p.AutoMusic = on;
            if (helper is not null)
                p.HelperId = HelperAutocomplete.Id(helper);
        });
        return Replies.Ephemeral(on ? $"🎵 On. A free helper{(helper is not null ? " (the one you picked, when it's free)" : "")} joins your voice channel, ready for `/play` or a voice command." : "🎵 Off.");
    }
}
