using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Voice.Hearing;

namespace THOBOTTO.Music;

// /helper: the helper in the member's voice channel switched for another, whatever it's there for: playing music
// (the queue and position go with it) or listening (summoned, talk time, auto-listen).
public sealed class HelperSwitchCommand(MusicService music, VoiceEars ears, VoicePresence presence) : GuildCommandModule
{
    [SlashCommand("helper", "Switch the helper in your voice channel for another, whatever it's there for", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> SwitchAsync(
        [SlashCommandParameter(Description = "Which helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string helper)
    {
        if (HelperAutocomplete.Id(helper) is not { } wanted)
            return Replies.Ephemeral("Pick a helper from the list.");
        if (!presence.Snapshot(GuildId).TryGetValue(Context.User.Id, out var where))
            return Replies.Ephemeral("Join a voice channel first.");
        string answer;
        if (music.PlayerIn(GuildId, where.ChannelId) is { } player)
            answer = await music.RefusalAsync(GuildId, (GuildUser)Context.User, player) ?? await music.SwapAsync(player, wanted);
        else if (ears.ListeningIn(GuildId).Any(l => l.ChannelId == where.ChannelId))
            answer = await ears.SwitchAsync(GuildId, where.ChannelId, wanted);
        else
            answer = "No helper is in your channel.";
        return new() { Content = answer, Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None };
    }
}
