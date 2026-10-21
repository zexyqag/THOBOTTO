using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Voice.Hearing;

namespace THOBOTTO;

public sealed partial class MeCommands
{
    [SubSlashCommand("voice-commands", "Let the helpers hear you: say a helper's name and what to do (\"Jeeves, skip\")")]
    public async Task<InteractionMessageProperties> VoiceCommandsAsync(
        [SlashCommandParameter(Description = "On or off")] bool on,
        [SlashCommandParameter(Name = "join-me", Description = "A free helper joins you to listen whenever you're in voice (if the server allows it)")] bool? joinMe = null)
    {
        if (!await Get<ModuleState>().IsEnabledAsync(GuildId, VoiceEars.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(VoiceEars.ModuleId));
        var allowed = (await Get<SettingsStore>().GetAsync<ListeningRules>(GuildId, VoiceEars.ModuleId)).AutoListenAllowed;
        await Get<VoicePrefs>().SetAsync(GuildId, Context.User.Id, p =>
        {
            p.Listen = on;
            p.AutoListen = on && (joinMe ?? p.AutoListen) && allowed;
        });
        return Replies.Ephemeral(!on ? "🎙️ Off. The helpers don't listen to you any more."
            : $"🎙️ On. {(joinMe == true && !allowed ? "This server doesn't let helpers join members by themselves, so " : "")}{(joinMe == true && allowed ? "A free helper joins you to listen whenever you're in voice." : "Summon a helper to listen with `/listen`.")} " +
              "Then say its name (or the playing helper's) and what to do: \"play …\", \"skip\", \"pause\", \"louder\", \"what's playing\". Only members who switched this on are listened to; nothing said is kept.");
    }
}
