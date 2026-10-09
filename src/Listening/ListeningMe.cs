using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Listening;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class MeCommands
{
    [SubSlashCommand("voice-commands", "Let the helpers hear you: say their name and what to do (\"Jeeves, skip\")")]
    public async Task<InteractionMessageProperties> VoiceCommandsAsync([SlashCommandParameter(Description = "On or off")] bool on)
    {
        var guildId = Context.Guild!.Id;
        if (!await Get<ModuleState>().IsEnabledAsync(guildId, VoiceEars.ModuleId))
            return Replies.Ephemeral($"The `{VoiceEars.ModuleId}` module is off here.");
        await Get<VoiceEars>().SetInAsync(guildId, Context.User.Id, on);
        return Replies.Ephemeral(on
            ? "🎙️ On. When music plays in your voice channel, say the helper's name and what to do: \"play …\", \"skip\", \"pause\", \"louder\", \"what's playing\". Only you and others who switched this on are listened to; nothing said is kept."
            : "🎙️ Off. The bot doesn't listen to you any more.");
    }
}
