using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Listening;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO;

public sealed partial class MeCommands
{
    [SubSlashCommand("voice-commands", "Let the helpers hear you: say a helper's name and what to do (\"Jeeves, skip\")")]
    public async Task<InteractionMessageProperties> VoiceCommandsAsync(
        [SlashCommandParameter(Description = "On or off")] bool on,
        [SlashCommandParameter(Name = "join-me", Description = "A free helper joins you to listen whenever you're in voice (if the server allows it)")] bool? joinMe = null)
    {
        var guildId = Context.Guild!.Id;
        if (!await Get<ModuleState>().IsEnabledAsync(guildId, VoiceEars.ModuleId))
            return Replies.Ephemeral($"The `{VoiceEars.ModuleId}` module is off here.");
        var allowed = (await Get<SettingsStore>().GetAsync<ListeningRules>(guildId, VoiceEars.ModuleId)).AutoListenAllowed;
        await Get<VoicePrefs>().SetAsync(guildId, Context.User.Id, p =>
        {
            p.Listen = on;
            p.AutoListen = on && (joinMe ?? p.AutoListen) && allowed;
        });
        return Replies.Ephemeral(!on ? "🎙️ Off. The helpers don't listen to you any more."
            : $"🎙️ On. {(joinMe == true && !allowed ? "This server doesn't let helpers join members by themselves, so " : "")}{(joinMe == true && allowed ? "A free helper joins you to listen whenever you're in voice." : "Summon a helper to listen with `/listen`.")} " +
              "Then say its name (or the playing helper's) and what to do: \"play …\", \"skip\", \"pause\", \"louder\", \"what's playing\". Only members who switched this on are listened to; nothing said is kept.");
    }

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
