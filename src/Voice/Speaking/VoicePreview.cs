using System.Text.RegularExpressions;

using THOBOTTO.Helpers;
using THOBOTTO.Panel;

namespace THOBOTTO.Voice.Speaking;

// A personality saying its hello in a voice (its own, or one being picked), for the personality page.
public static partial class VoicePreview
{
    public static string Link(ulong guildId, long personalityId) => $"/g/{guildId}/helpers/{personalityId}/voice";

    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long id, string? voice, PanelAccess access, PersonalityBook book, HelperVoices voices)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await book.FindAsync(guildId, id) is not { } personality || !voices.On)
            return Results.NotFound();
        var line = (personality.Phrases.GetValueOrDefault(Moments.Joined) ?? PersonalityFile.Plain.Lines[Moments.Joined]).FirstOrDefault() ?? "Hello.";
        line = line.Replace("{channel}", "General").Replace("{user}", "Ana").Replace("{track}", "Thunderstruck").Replace("{helper}", personality.Name);
        var chosen = await voices.UsableVoiceAsync(voice is not null && VoiceName().IsMatch(voice) ? voice : voices.VoiceOf(personality));
        return await voices.SayAsync(line, chosen, VoiceEffects.From(personality.Effects), context.RequestAborted) is { } spoken
            ? Results.File(Pcm.Wav(spoken), "audio/wav")
            : Results.NotFound();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:+()-]{1,64}$")]
    private static partial Regex VoiceName();
}
