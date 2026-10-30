using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Sounds;

// "Hit us with the airhorn": a sound from the server's soundboard, played where the speaker is.
public sealed class SoundVoiceActions(SoundBoard sounds) : IVoiceActions
{
    public IEnumerable<VoiceAction> Actions =>
    [
        new("sound", "Play a sound from the server's soundboard.",
            new() { ["name"] = Schema.Text("The sound's name") }, ["name"],
            async (request, args) => VoicePlan.Done(await sounds.PlayAsync(request.Heard.GuildId, request.Heard.UserId, Schema.String(args, "name") ?? "")),
            ("hit us with the airhorn", """{"name":"airhorn"}""")),
    ];
}
