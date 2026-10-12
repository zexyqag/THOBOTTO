using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO;

public sealed partial class MeCommands
{
    [SubSlashCommand("music-join", "A free helper joins you for music whenever you're in voice, ready to play (if the server allows it)")]
    public async Task<InteractionMessageProperties> MusicJoinAsync([SlashCommandParameter(Description = "On or off")] bool on)
    {
        var guildId = Context.Guild!.Id;
        if (on && !(await Get<SettingsStore>().GetAsync<MusicRules>(guildId, MusicService.ModuleId)).AutoJoinAllowed)
            return Replies.Ephemeral("This server doesn't let helpers join members by themselves.");
        await Get<VoicePrefs>().SetAsync(guildId, Context.User.Id, p => p.AutoMusic = on);
        return Replies.Ephemeral(on ? "🎵 On. A free helper joins your voice channel, ready for `/play` or a voice command." : "🎵 Off.");
    }
}
