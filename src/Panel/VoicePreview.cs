using System.Text.RegularExpressions;

using THOBOTTO.Helpers;
using THOBOTTO.Speaking;

namespace THOBOTTO.Panel;

// A personality saying its hello in a voice (its own, or one being picked), for the personality page.
public static partial class VoicePreview
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long id, string? voice, PanelAccess access, PersonalityBook book, HelperVoices voices)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await book.FindAsync(guildId, id) is not { } personality || !voices.On)
            return Results.NotFound();
        var line = (personality.Phrases.GetValueOrDefault(Moments.Joined) ?? PersonalityFile.Plain.Lines[Moments.Joined]).FirstOrDefault() ?? "Hello.";
        line = line.Replace("{channel}", "General").Replace("{user}", "Ana").Replace("{track}", "Thunderstruck").Replace("{helper}", personality.Name);
        var chosen = voice is not null && VoiceName().IsMatch(voice) ? voice : voices.VoiceOf(personality);
        if (await voices.VoicesAsync() is { Count: > 0 } known && !known.Contains(chosen))
            chosen = voices.DefaultVoice;
        return await voices.SayAsync(HelperVoices.Speakable(line), chosen, context.RequestAborted) is { } spoken
            ? Results.File(Pcm.Wav(spoken), "audio/wav")
            : Results.NotFound();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex VoiceName();
}
