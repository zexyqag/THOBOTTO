using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Music;
using THOBOTTO.Voice.Hearing;

namespace THOBOTTO.Voice.Lines;

// /helper: the helper in the member's voice channel switched for another, whatever it's there for (playing music,
// with the queue and position; listening), and lines it says in the member's name.
[SlashCommand("helper", "Helpers: switch the one in your call, or have it say or ask something in your name", Contexts = [InteractionContextType.Guild])]
public sealed class HelperCommands(MusicService music, VoiceEars ears, VoicePresence presence, HelperLines lines) : GuildCommandModule
{
    [SubSlashCommand("switch", "Switch the helper in your voice channel for another, whatever it's there for")]
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

    [SubSlashCommand("say", "A helper in your call says it in your name (\"Ana says: …\"); points, and a wait between lines")]
    public Task SayAsync([SlashCommandParameter(Description = "What it should say", MaxLength = HelperLines.MostChars)] string text) => LineAsync(text, asks: false);

    [SubSlashCommand("ask", "A helper in your call asks it in your name (\"Ana asks: …\"); points, and a wait between lines")]
    public Task AskAsync([SlashCommandParameter(Description = "What it should ask", MaxLength = HelperLines.MostChars)] string text) => LineAsync(text, asks: true);

    // A free helper hopping in takes a few seconds, longer than Discord waits for a reply.
    private async Task LineAsync(string text, bool asks)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var answer = presence.Snapshot(GuildId).TryGetValue(Context.User.Id, out var where)
            ? await lines.SayAsync(GuildId, (GuildUser)Context.User, where.ChannelId, text, asks)
            : "Join a voice channel first.";
        await ModifyResponseAsync(m =>
        {
            m.Content = answer;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }
}
